using Npgsql;
using OGABudget.Api.Infrastructure;
using OGABudget.Api.Models;

namespace OGABudget.Api.Services;

/// <summary>Objectifs d'épargne et versements associés.</summary>
public class ObjectifService
{
    private readonly NpgsqlDataSource _db;
    private readonly TransactionService _transactions;

    public ObjectifService(NpgsqlDataSource db, TransactionService transactions)
    {
        _db = db;
        _transactions = transactions;
    }

    private const string Selection =
        """
        SELECT o.id, o.nom, o.description, o.montant_cible,
               COALESCE((SELECT SUM(v.montant) FROM objectif_versements v WHERE v.objectif_id = o.id), 0),
               o.date_echeance, o.compte_id, c.nom, o.couleur, o.icone, o.statut::text, o.date_creation
        FROM objectifs o
        LEFT JOIN comptes c ON c.id = o.compte_id
        """;

    public async Task<List<ObjectifDto>> ListerAsync(Guid utilisateurId, string? statut, CancellationToken ct)
    {
        var filtre = statut is "en_cours" or "atteint" or "abandonne" ? " AND o.statut = @statut::statut_objectif" : "";

        await using var cmd = _db.CreateCommand(
            $"{Selection} WHERE o.utilisateur_id = @uid{filtre} ORDER BY o.statut, o.date_echeance NULLS LAST, o.nom");
        cmd.Ajouter("uid", utilisateurId);
        if (filtre.Length > 0) cmd.Ajouter("statut", statut);

        var liste = new List<ObjectifDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) liste.Add(Lire(reader));
        return liste;
    }

    public async Task<ObjectifDto?> ObtenirAsync(Guid utilisateurId, Guid id, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand($"{Selection} WHERE o.id = @id AND o.utilisateur_id = @uid");
        cmd.Ajouter("id", id);
        cmd.Ajouter("uid", utilisateurId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Lire(reader) : null;
    }

    public async Task<Guid?> CreerAsync(Guid utilisateurId, CreerObjectifRequest req, CancellationToken ct)
    {
        if (req.MontantCible <= 0) return null;

        await using var cmd = _db.CreateCommand(
            """
            INSERT INTO objectifs (utilisateur_id, compte_id, nom, description, montant_cible,
                                   date_echeance, couleur, icone)
            SELECT @uid, @compte, @nom, @description, @cible, @echeance,
                   COALESCE(@couleur, '#ffb400'), COALESCE(@icone, 'target')
            WHERE @compte IS NULL
               OR EXISTS (SELECT 1 FROM comptes WHERE id = @compte AND utilisateur_id = @uid)
            RETURNING id
            """);
        cmd.Ajouter("uid", utilisateurId);
        cmd.Ajouter("compte", req.CompteId);
        cmd.Ajouter("nom", req.Nom.Trim());
        cmd.Ajouter("description", Vide(req.Description));
        cmd.Ajouter("cible", req.MontantCible);
        cmd.Ajouter("echeance", req.DateEcheance);
        cmd.Ajouter("couleur", Vide(req.Couleur));
        cmd.Ajouter("icone", Vide(req.Icone));

        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    public async Task<bool> MettreAJourAsync(Guid utilisateurId, Guid id, MajObjectifRequest req, CancellationToken ct)
    {
        var statut = req.Statut?.ToLowerInvariant();
        if (statut is not ("en_cours" or "atteint" or "abandonne")) statut = "en_cours";

        await using var cmd = _db.CreateCommand(
            """
            UPDATE objectifs SET
                nom = @nom, description = @description, montant_cible = @cible,
                date_echeance = @echeance, compte_id = @compte,
                couleur = COALESCE(@couleur, couleur), icone = COALESCE(@icone, icone),
                statut = @statut::statut_objectif
            WHERE id = @id AND utilisateur_id = @uid
            """);
        cmd.Ajouter("id", id);
        cmd.Ajouter("uid", utilisateurId);
        cmd.Ajouter("nom", req.Nom.Trim());
        cmd.Ajouter("description", Vide(req.Description));
        cmd.Ajouter("cible", req.MontantCible);
        cmd.Ajouter("echeance", req.DateEcheance);
        cmd.Ajouter("compte", req.CompteId);
        cmd.Ajouter("couleur", Vide(req.Couleur));
        cmd.Ajouter("icone", Vide(req.Icone));
        cmd.Ajouter("statut", statut);

        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> SupprimerAsync(Guid utilisateurId, Guid id, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("DELETE FROM objectifs WHERE id = @id AND utilisateur_id = @uid");
        cmd.Ajouter("id", id);
        cmd.Ajouter("uid", utilisateurId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    // ─── Versements ─────────────────────────────────────────────────────────

    public async Task<List<VersementDto>> ListerVersementsAsync(Guid utilisateurId, Guid objectifId,
                                                                CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand(
            """
            SELECT v.id, v.objectif_id, v.montant, v.date_versement, v.note, v.transaction_id, v.date_creation
            FROM objectif_versements v
            JOIN objectifs o ON o.id = v.objectif_id AND o.utilisateur_id = @uid
            WHERE v.objectif_id = @objectif
            ORDER BY v.date_versement DESC, v.date_creation DESC
            """);
        cmd.Ajouter("uid", utilisateurId);
        cmd.Ajouter("objectif", objectifId);

        var liste = new List<VersementDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            liste.Add(new VersementDto
            {
                Id = reader.GetGuid(0),
                ObjectifId = reader.GetGuid(1),
                Montant = reader.Decimal(2),
                DateVersement = reader.GetFieldValue<DateOnly>(3),
                Note = reader.Texte(4),
                TransactionId = reader.Uuid(5),
                DateCreation = reader.Horodatage(6)
            });
        return liste;
    }

    /// <summary>
    /// Enregistre un versement. Quand <c>GenererTransaction</c> est demandé, un transfert
    /// réel est créé du compte source vers le compte d'épargne de l'objectif : le solde des
    /// comptes et l'avancement de l'objectif restent cohérents.
    /// </summary>
    public async Task<(VersementDto? versement, string? erreur, bool introuvable)> AjouterVersementAsync(
        Guid utilisateurId, Guid objectifId, CreerVersementRequest req, CancellationToken ct)
    {
        if (req.Montant == 0) return (null, "Le montant du versement ne peut pas être nul.", false);

        await using var conn = await _db.OpenConnectionAsync(ct);

        Guid? compteObjectif;
        string nomObjectif;
        await using (var cmd = new NpgsqlCommand(
            "SELECT compte_id, nom FROM objectifs WHERE id = @id AND utilisateur_id = @uid", conn))
        {
            cmd.Ajouter("id", objectifId);
            cmd.Ajouter("uid", utilisateurId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return (null, null, true);
            compteObjectif = reader.Uuid(0);
            nomObjectif = reader.GetString(1);
        }

        Guid? transactionId = null;
        await using var tx = await conn.BeginTransactionAsync(ct);

        if (req.GenererTransaction)
        {
            if (compteObjectif is not Guid destination)
                return (null, "L'objectif n'est rattaché à aucun compte d'épargne : impossible de générer le transfert.", false);
            if (req.CompteSourceId is not Guid source)
                return (null, "Compte source obligatoire pour générer le transfert.", false);
            if (source == destination)
                return (null, "Le compte source doit différer du compte d'épargne de l'objectif.", false);
            if (req.Montant < 0)
                return (null, "Un retrait ne peut pas générer automatiquement de transfert.", false);

            await using (var verif = new NpgsqlCommand(
                "SELECT 1 FROM comptes WHERE id = @id AND utilisateur_id = @uid", conn, tx))
            {
                verif.Ajouter("id", source);
                verif.Ajouter("uid", utilisateurId);
                if (await verif.ExecuteScalarAsync(ct) == null)
                    return (null, "Compte source introuvable.", false);
            }

            transactionId = await _transactions.InsererAsync(conn, tx, utilisateurId, new CreerTransactionRequest
            {
                Type = "transfert",
                Montant = req.Montant,
                CompteId = source,
                CompteDestinationId = destination,
                DateOperation = req.DateVersement,
                Libelle = $"Épargne — {nomObjectif}",
                Note = Vide(req.Note)
            }, null, ct);
        }

        VersementDto versement;
        await using (var cmd = new NpgsqlCommand(
            """
            INSERT INTO objectif_versements (objectif_id, transaction_id, montant, date_versement, note)
            VALUES (@objectif, @transaction, @montant, COALESCE(@date, CURRENT_DATE), @note)
            RETURNING id, objectif_id, montant, date_versement, note, transaction_id, date_creation
            """, conn, tx))
        {
            cmd.Ajouter("objectif", objectifId);
            cmd.Ajouter("transaction", transactionId);
            cmd.Ajouter("montant", req.Montant);
            cmd.Ajouter("date", req.DateVersement);
            cmd.Ajouter("note", Vide(req.Note));

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            versement = new VersementDto
            {
                Id = reader.GetGuid(0),
                ObjectifId = reader.GetGuid(1),
                Montant = reader.Decimal(2),
                DateVersement = reader.GetFieldValue<DateOnly>(3),
                Note = reader.Texte(4),
                TransactionId = reader.Uuid(5),
                DateCreation = reader.Horodatage(6)
            };
        }

        // Atteindre la cible bascule l'objectif : l'utilisateur n'a pas à le faire à la main.
        await using (var cmd = new NpgsqlCommand(
            """
            UPDATE objectifs SET statut = 'atteint'
            WHERE id = @id AND statut = 'en_cours'
              AND (SELECT COALESCE(SUM(montant), 0) FROM objectif_versements WHERE objectif_id = @id) >= montant_cible
            """, conn, tx))
        {
            cmd.Ajouter("id", objectifId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return (versement, null, false);
    }

    public async Task<bool> SupprimerVersementAsync(Guid utilisateurId, Guid objectifId, Guid versementId,
                                                    CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand(
            """
            DELETE FROM objectif_versements v
            USING objectifs o
            WHERE v.id = @id AND v.objectif_id = @objectif
              AND o.id = v.objectif_id AND o.utilisateur_id = @uid
            """);
        cmd.Ajouter("id", versementId);
        cmd.Ajouter("objectif", objectifId);
        cmd.Ajouter("uid", utilisateurId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    // ─── Interne ────────────────────────────────────────────────────────────

    private static ObjectifDto Lire(NpgsqlDataReader r)
    {
        var cible = r.Decimal(3);
        var actuel = r.Decimal(4);
        var echeance = r.DateNullable(5);
        var aujourdhui = DateOnly.FromDateTime(DateTime.UtcNow);
        var restant = Math.Max(0, cible - actuel);

        int? jours = echeance is DateOnly d ? d.DayNumber - aujourdhui.DayNumber : null;

        // Effort mensuel : ce qu'il reste à épargner, étalé sur les mois entiers restants.
        decimal? effort = null;
        if (echeance is DateOnly fin && restant > 0)
        {
            var mois = Math.Max(1, (fin.Year - aujourdhui.Year) * 12 + fin.Month - aujourdhui.Month);
            effort = Math.Round(restant / mois, 2);
        }

        return new ObjectifDto
        {
            Id = r.GetGuid(0),
            Nom = r.GetString(1),
            Description = r.Texte(2),
            MontantCible = cible,
            MontantActuel = actuel,
            MontantRestant = restant,
            PourcentageAtteint = cible == 0 ? 0 : Math.Round(actuel * 100m / cible, 1),
            DateEcheance = echeance,
            JoursRestants = jours,
            EffortMensuelRequis = effort,
            CompteId = r.Uuid(6),
            CompteNom = r.Texte(7),
            Couleur = r.GetString(8),
            Icone = r.GetString(9),
            Statut = r.GetString(10),
            DateCreation = r.Horodatage(11)
        };
    }

    private static string? Vide(string? valeur) => string.IsNullOrWhiteSpace(valeur) ? null : valeur.Trim();
}
