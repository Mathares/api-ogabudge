using Npgsql;
using OGABudget.Api.Infrastructure;
using OGABudget.Api.Models;

namespace OGABudget.Api.Services;

/// <summary>
/// Opérations qui reviennent : salaire, loyer, abonnements. La génération est
/// idempotente — relancer deux fois le même jour ne crée pas de doublon, car
/// <c>prochaine_echeance</c> avance à chaque transaction matérialisée.
/// </summary>
public class RecurrenceService
{
    private readonly NpgsqlDataSource _db;
    private readonly TransactionService _transactions;
    private readonly ILogger<RecurrenceService> _logger;

    public RecurrenceService(NpgsqlDataSource db, TransactionService transactions,
                             ILogger<RecurrenceService> logger)
    {
        _db = db;
        _transactions = transactions;
        _logger = logger;
    }

    private const string Selection =
        """
        SELECT r.id, r.libelle, r.note, r.type::text, r.montant, r.compte_id, c.nom,
               r.categorie_id, cat.nom, r.frequence::text, r.intervalle, r.jour_du_mois,
               r.date_debut, r.date_fin, r.prochaine_echeance, r.auto_generer, r.actif, r.date_creation
        FROM recurrences r
        JOIN comptes c ON c.id = r.compte_id
        LEFT JOIN categories cat ON cat.id = r.categorie_id
        """;

    public async Task<List<RecurrenceDto>> ListerAsync(Guid utilisateurId, bool inclureInactives,
                                                       CancellationToken ct)
    {
        var filtre = inclureInactives ? "" : " AND r.actif = true";
        await using var cmd = _db.CreateCommand(
            $"{Selection} WHERE r.utilisateur_id = @uid{filtre} ORDER BY r.actif DESC, r.prochaine_echeance");
        cmd.Ajouter("uid", utilisateurId);

        var liste = new List<RecurrenceDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) liste.Add(Lire(reader));
        return liste;
    }

    /// <summary>Échéances tombant dans les <paramref name="jours"/> prochains jours, pour l'écran d'accueil.</summary>
    public async Task<List<RecurrenceDto>> ProchainesAsync(Guid utilisateurId, int jours, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand(
            $"""
             {Selection}
             WHERE r.utilisateur_id = @uid AND r.actif = true
               AND r.prochaine_echeance <= CURRENT_DATE + make_interval(days => @jours)
             ORDER BY r.prochaine_echeance
             LIMIT 10
             """);
        cmd.Ajouter("uid", utilisateurId);
        cmd.Ajouter("jours", jours);

        var liste = new List<RecurrenceDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) liste.Add(Lire(reader));
        return liste;
    }

    public async Task<RecurrenceDto?> ObtenirAsync(Guid utilisateurId, Guid id, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand($"{Selection} WHERE r.id = @id AND r.utilisateur_id = @uid");
        cmd.Ajouter("id", id);
        cmd.Ajouter("uid", utilisateurId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Lire(reader) : null;
    }

    public async Task<(Guid? id, string? erreur)> CreerAsync(Guid utilisateurId, CreerRecurrenceRequest req,
                                                             CancellationToken ct)
    {
        var erreur = Valider(req);
        if (erreur != null) return (null, erreur);

        var debut = req.DateDebut ?? DateOnly.FromDateTime(DateTime.UtcNow);

        await using var cmd = _db.CreateCommand(
            """
            INSERT INTO recurrences (utilisateur_id, compte_id, categorie_id, type, montant, libelle, note,
                                     frequence, intervalle, jour_du_mois, date_debut, date_fin,
                                     prochaine_echeance, auto_generer)
            SELECT @uid, @compte, @categorie, @type::type_flux, @montant, @libelle, @note,
                   @frequence::frequence_recurrence, @intervalle, @jour, @debut, @fin, @debut, @auto
            WHERE EXISTS (SELECT 1 FROM comptes WHERE id = @compte AND utilisateur_id = @uid)
              AND (@categorie IS NULL
                   OR EXISTS (SELECT 1 FROM categories
                              WHERE id = @categorie AND utilisateur_id = @uid AND type = @type::type_flux))
            RETURNING id
            """);
        cmd.Ajouter("uid", utilisateurId);
        cmd.Ajouter("compte", req.CompteId);
        cmd.Ajouter("categorie", req.CategorieId);
        cmd.Ajouter("type", req.Type.ToLowerInvariant());
        cmd.Ajouter("montant", req.Montant);
        cmd.Ajouter("libelle", req.Libelle.Trim());
        cmd.Ajouter("note", Vide(req.Note));
        cmd.Ajouter("frequence", req.Frequence.ToLowerInvariant());
        cmd.Ajouter("intervalle", (short)Math.Clamp(req.Intervalle, 1, 52));
        cmd.Ajouter("jour", req.JourDuMois is int j ? (short)j : null);
        cmd.Ajouter("debut", debut);
        cmd.Ajouter("fin", req.DateFin);
        cmd.Ajouter("auto", req.AutoGenerer);

        if (await cmd.ExecuteScalarAsync(ct) is not Guid id)
            return (null, "Compte ou catégorie introuvable, ou catégorie de type incompatible.");
        return (id, null);
    }

    public async Task<(bool ok, string? erreur)> MettreAJourAsync(Guid utilisateurId, Guid id,
                                                                  MajRecurrenceRequest req, CancellationToken ct)
    {
        var erreur = Valider(req);
        if (erreur != null) return (false, erreur);

        await using var cmd = _db.CreateCommand(
            """
            UPDATE recurrences SET
                compte_id = @compte, categorie_id = @categorie, type = @type::type_flux,
                montant = @montant, libelle = @libelle, note = @note,
                frequence = @frequence::frequence_recurrence, intervalle = @intervalle,
                jour_du_mois = @jour, date_debut = COALESCE(@debut, date_debut), date_fin = @fin,
                auto_generer = @auto, actif = @actif
            WHERE id = @id AND utilisateur_id = @uid
              AND EXISTS (SELECT 1 FROM comptes WHERE id = @compte AND utilisateur_id = @uid)
            """);
        cmd.Ajouter("id", id);
        cmd.Ajouter("uid", utilisateurId);
        cmd.Ajouter("compte", req.CompteId);
        cmd.Ajouter("categorie", req.CategorieId);
        cmd.Ajouter("type", req.Type.ToLowerInvariant());
        cmd.Ajouter("montant", req.Montant);
        cmd.Ajouter("libelle", req.Libelle.Trim());
        cmd.Ajouter("note", Vide(req.Note));
        cmd.Ajouter("frequence", req.Frequence.ToLowerInvariant());
        cmd.Ajouter("intervalle", (short)Math.Clamp(req.Intervalle, 1, 52));
        cmd.Ajouter("jour", req.JourDuMois is int j ? (short)j : null);
        cmd.Ajouter("debut", req.DateDebut);
        cmd.Ajouter("fin", req.DateFin);
        cmd.Ajouter("auto", req.AutoGenerer);
        cmd.Ajouter("actif", req.Actif);

        return (await cmd.ExecuteNonQueryAsync(ct) > 0, null);
    }

    public async Task<bool> SupprimerAsync(Guid utilisateurId, Guid id, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("DELETE FROM recurrences WHERE id = @id AND utilisateur_id = @uid");
        cmd.Ajouter("id", id);
        cmd.Ajouter("uid", utilisateurId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    // ─── Génération ─────────────────────────────────────────────────────────

    /// <summary>
    /// Matérialise toutes les échéances échues jusqu'à aujourd'hui. Passer
    /// <paramref name="utilisateurId"/> à null traite l'ensemble des utilisateurs
    /// (appel du service de fond).
    /// </summary>
    public async Task<GenerationRecurrencesDto> GenererAsync(Guid? utilisateurId, CancellationToken ct)
    {
        var aujourdhui = DateOnly.FromDateTime(DateTime.UtcNow);
        var resultat = new GenerationRecurrencesDto();

        await using var conn = await _db.OpenConnectionAsync(ct);

        var aTraiter = new List<(Guid id, Guid utilisateur, Guid compte, Guid? categorie, string type,
                                 decimal montant, string libelle, string? note, string frequence,
                                 int intervalle, int? jour, DateOnly echeance, DateOnly? fin)>();

        var filtre = utilisateurId is null ? "" : " AND utilisateur_id = @uid";
        await using (var cmd = new NpgsqlCommand(
            $"""
             SELECT id, utilisateur_id, compte_id, categorie_id, type::text, montant, libelle, note,
                    frequence::text, intervalle, jour_du_mois, prochaine_echeance, date_fin
             FROM recurrences
             WHERE actif = true AND auto_generer = true AND prochaine_echeance <= @aujourdhui{filtre}
             """, conn))
        {
            cmd.Ajouter("aujourdhui", aujourdhui);
            if (utilisateurId is Guid uid) cmd.Ajouter("uid", uid);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                aTraiter.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.Uuid(3),
                              reader.GetString(4), reader.Decimal(5), reader.GetString(6), reader.Texte(7),
                              reader.GetString(8), reader.Entier(9), reader.EntierNullable(10),
                              reader.GetFieldValue<DateOnly>(11), reader.DateNullable(12)));
        }

        var identifiantsCrees = new List<Guid>();

        foreach (var r in aTraiter)
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            var echeance = r.echeance;
            var creees = 0;

            // Une appli ouverte après trois mois d'absence doit rattraper les trois loyers.
            while (echeance <= aujourdhui && (r.fin is null || echeance <= r.fin) && creees < 60)
            {
                identifiantsCrees.Add(await _transactions.InsererAsync(conn, tx, r.utilisateur, new CreerTransactionRequest
                {
                    Type = r.type,
                    Montant = r.montant,
                    CompteId = r.compte,
                    CategorieId = r.categorie,
                    DateOperation = echeance,
                    Libelle = r.libelle,
                    Note = r.note
                }, r.id, ct));

                creees++;
                echeance = Periodes.ProchaineEcheance(echeance, r.frequence, r.intervalle, r.jour);
            }

            var termine = r.fin is DateOnly fin && echeance > fin;
            await using (var maj = new NpgsqlCommand(
                "UPDATE recurrences SET prochaine_echeance = @echeance, actif = @actif WHERE id = @id",
                conn, tx))
            {
                maj.Ajouter("echeance", echeance);
                maj.Ajouter("actif", !termine);
                maj.Ajouter("id", r.id);
                await maj.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            resultat.TransactionsCreees += creees;
        }

        if (resultat.TransactionsCreees > 0)
            _logger.LogInformation("{Nombre} transaction(s) générées depuis {Recurrences} récurrence(s).",
                                   resultat.TransactionsCreees, aTraiter.Count);

        // Le detail n'est renvoye que pour un utilisateur donne : le service de fond
        // traite tout le parc et n'a que faire des DTO.
        if (utilisateurId is Guid u)
            foreach (var id in identifiantsCrees)
                if (await _transactions.ObtenirAsync(u, id, ct) is { } dto)
                    resultat.Transactions.Add(dto);

        return resultat;
    }

    // ─── Interne ────────────────────────────────────────────────────────────

    private static string? Valider(CreerRecurrenceRequest req)
    {
        if (req.Type?.ToLowerInvariant() is not ("depense" or "revenu"))
            return "Une récurrence porte une dépense ou un revenu, pas un transfert.";
        if (req.Montant <= 0)
            return "Le montant doit être strictement positif.";
        if (req.Frequence?.ToLowerInvariant() is not
            ("quotidienne" or "hebdomadaire" or "mensuelle" or "trimestrielle" or "annuelle"))
            return "Fréquence invalide.";
        if (req.DateFin is DateOnly fin && req.DateDebut is DateOnly debut && fin < debut)
            return "La date de fin précède la date de début.";
        return null;
    }

    private static RecurrenceDto Lire(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        Libelle = r.GetString(1),
        Note = r.Texte(2),
        Type = r.GetString(3),
        Montant = r.Decimal(4),
        CompteId = r.GetGuid(5),
        CompteNom = r.GetString(6),
        CategorieId = r.Uuid(7),
        CategorieNom = r.Texte(8),
        Frequence = r.GetString(9),
        Intervalle = r.Entier(10),
        JourDuMois = r.EntierNullable(11),
        DateDebut = r.GetFieldValue<DateOnly>(12),
        DateFin = r.DateNullable(13),
        ProchaineEcheance = r.GetFieldValue<DateOnly>(14),
        AutoGenerer = r.GetBoolean(15),
        Actif = r.GetBoolean(16),
        DateCreation = r.Horodatage(17)
    };

    private static string? Vide(string? valeur) => string.IsNullOrWhiteSpace(valeur) ? null : valeur.Trim();
}
