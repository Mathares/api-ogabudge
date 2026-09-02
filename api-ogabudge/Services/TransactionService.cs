using Npgsql;
using OGABudget.Api.Infrastructure;
using OGABudget.Api.Models;

namespace OGABudget.Api.Services;

/// <summary>Saisie et consultation des opérations : dépenses, revenus et transferts.</summary>
public class TransactionService
{
    private readonly NpgsqlDataSource _db;

    public TransactionService(NpgsqlDataSource db) => _db = db;

    private const string Selection =
        """
        SELECT t.id, t.type::text, t.montant, t.devise::text, t.date_operation, t.libelle, t.note,
               t.tiers, t.mode_paiement, t.piece_jointe_url, t.pointee,
               t.compte_id, c.nom, t.compte_destination_id, cd.nom,
               t.categorie_id, cat.nom, cat.icone, cat.couleur,
               t.recurrence_id, t.date_creation
        FROM transactions t
        JOIN comptes c ON c.id = t.compte_id
        LEFT JOIN comptes cd ON cd.id = t.compte_destination_id
        LEFT JOIN categories cat ON cat.id = t.categorie_id
        """;

    // ─── Lecture ────────────────────────────────────────────────────────────

    public async Task<PageResultat<TransactionDto>> ListerAsync(Guid utilisateurId, FiltreTransactions filtre,
                                                                CancellationToken ct)
    {
        var page = Math.Max(1, filtre.Page);
        var taille = Math.Clamp(filtre.TaillePage, 1, 200);

        var (where, parametres) = ConstruireFiltre(utilisateurId, filtre);

        await using var conn = await _db.OpenConnectionAsync(ct);

        int total;
        await using (var cmd = new NpgsqlCommand($"SELECT COUNT(*) FROM transactions t WHERE {where}", conn))
        {
            foreach (var (nom, valeur) in parametres) cmd.Ajouter(nom, valeur);
            total = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }

        var elements = new List<TransactionDto>();
        await using (var cmd = new NpgsqlCommand(
            $"""
             {Selection}
             WHERE {where}
             ORDER BY t.date_operation DESC, t.date_creation DESC
             LIMIT @limite OFFSET @decalage
             """, conn))
        {
            foreach (var (nom, valeur) in parametres) cmd.Ajouter(nom, valeur);
            cmd.Ajouter("limite", taille);
            cmd.Ajouter("decalage", (page - 1) * taille);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) elements.Add(Lire(reader));
        }

        return new PageResultat<TransactionDto>
        {
            Elements = elements,
            Page = page,
            TaillePage = taille,
            TotalElements = total
        };
    }

    public async Task<TransactionDto?> ObtenirAsync(Guid utilisateurId, Guid id, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand($"{Selection} WHERE t.id = @id AND t.utilisateur_id = @uid");
        cmd.Ajouter("id", id);
        cmd.Ajouter("uid", utilisateurId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Lire(reader) : null;
    }

    // ─── Écriture ───────────────────────────────────────────────────────────

    /// <returns>La transaction créée, ou un message d'erreur métier si la demande est incohérente.</returns>
    public async Task<(TransactionDto? transaction, string? erreur)> CreerAsync(
        Guid utilisateurId, CreerTransactionRequest req, CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        var erreur = await ValiderAsync(conn, null, utilisateurId, req, ct);
        if (erreur != null) return (null, erreur);

        var id = await InsererAsync(conn, null, utilisateurId, req, null, ct);
        return (await ObtenirAsync(utilisateurId, id, ct), null);
    }

    public async Task<(TransactionDto? transaction, string? erreur, bool introuvable)> MettreAJourAsync(
        Guid utilisateurId, Guid id, MajTransactionRequest req, CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        var erreur = await ValiderAsync(conn, null, utilisateurId, req, ct);
        if (erreur != null) return (null, erreur, false);

        await using (var cmd = new NpgsqlCommand(
            """
            UPDATE transactions SET
                type = @type::type_flux, montant = @montant, compte_id = @compte,
                compte_destination_id = @destination, categorie_id = @categorie,
                devise = (SELECT devise FROM comptes WHERE id = @compte),
                date_operation = @date, libelle = @libelle, note = @note, tiers = @tiers,
                mode_paiement = @mode, piece_jointe_url = @piece, pointee = @pointee
            WHERE id = @id AND utilisateur_id = @uid
            """, conn))
        {
            RemplirParametres(cmd, req);
            cmd.Ajouter("id", id);
            cmd.Ajouter("uid", utilisateurId);
            if (await cmd.ExecuteNonQueryAsync(ct) == 0) return (null, null, true);
        }

        return (await ObtenirAsync(utilisateurId, id, ct), null, false);
    }

    public async Task<bool> SupprimerAsync(Guid utilisateurId, Guid id, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand(
            "DELETE FROM transactions WHERE id = @id AND utilisateur_id = @uid");
        cmd.Ajouter("id", id);
        cmd.Ajouter("uid", utilisateurId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> PointerAsync(Guid utilisateurId, Guid id, bool pointee, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand(
            "UPDATE transactions SET pointee = @pointee WHERE id = @id AND utilisateur_id = @uid");
        cmd.Ajouter("pointee", pointee);
        cmd.Ajouter("id", id);
        cmd.Ajouter("uid", utilisateurId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>
    /// Envoi groupé après une saisie hors ligne. Tout le lot est validé avant écriture :
    /// une seule ligne invalide annule l'ensemble, ce qui évite au mobile de deviner
    /// quelles opérations sont réellement passées.
    /// </summary>
    public async Task<(List<TransactionDto> creees, string? erreur)> CreerLotAsync(
        Guid utilisateurId, List<CreerTransactionRequest> demandes, CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var identifiants = new List<Guid>();
        for (var i = 0; i < demandes.Count; i++)
        {
            var erreur = await ValiderAsync(conn, tx, utilisateurId, demandes[i], ct);
            if (erreur != null)
            {
                await tx.RollbackAsync(ct);
                return (new List<TransactionDto>(), $"Ligne {i + 1} : {erreur}");
            }
            identifiants.Add(await InsererAsync(conn, tx, utilisateurId, demandes[i], null, ct));
        }

        await tx.CommitAsync(ct);

        var creees = new List<TransactionDto>();
        foreach (var id in identifiants)
            if (await ObtenirAsync(utilisateurId, id, ct) is { } dto) creees.Add(dto);
        return (creees, null);
    }

    // ─── Interne ────────────────────────────────────────────────────────────

    /// <summary>Insertion partagée par la création unitaire, les lots et la génération des récurrences.</summary>
    internal async Task<Guid> InsererAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid utilisateurId,
                                           CreerTransactionRequest req, Guid? recurrenceId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO transactions (utilisateur_id, compte_id, compte_destination_id, categorie_id,
                                      recurrence_id, type, montant, devise, date_operation, libelle,
                                      note, tiers, mode_paiement, piece_jointe_url, pointee)
            VALUES (@uid, @compte, @destination, @categorie, @recurrence, @type::type_flux, @montant,
                    (SELECT devise FROM comptes WHERE id = @compte),
                    @date, @libelle, @note, @tiers, @mode, @piece, @pointee)
            RETURNING id
            """, conn, tx);

        RemplirParametres(cmd, req);
        cmd.Ajouter("uid", utilisateurId);
        cmd.Ajouter("recurrence", recurrenceId);
        return (Guid)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static void RemplirParametres(NpgsqlCommand cmd, CreerTransactionRequest req)
    {
        var type = req.Type.ToLowerInvariant();
        cmd.Ajouter("type", type);
        cmd.Ajouter("montant", req.Montant);
        cmd.Ajouter("compte", req.CompteId);
        cmd.Ajouter("destination", type == "transfert" ? req.CompteDestinationId : null);
        cmd.Ajouter("categorie", type == "transfert" ? null : req.CategorieId);
        cmd.Ajouter("date", req.DateOperation ?? DateOnly.FromDateTime(DateTime.UtcNow));
        cmd.Ajouter("libelle", req.Libelle.Trim());
        cmd.Ajouter("note", Vide(req.Note));
        cmd.Ajouter("tiers", Vide(req.Tiers));
        cmd.Ajouter("mode", Vide(req.ModePaiement));
        cmd.Ajouter("piece", Vide(req.PieceJointeUrl));
        cmd.Ajouter("pointee", req.Pointee);
    }

    /// <returns>Un message en français destiné à l'utilisateur, ou <c>null</c> si la demande est valide.</returns>
    private static async Task<string?> ValiderAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid utilisateurId,
                                                    CreerTransactionRequest req, CancellationToken ct)
    {
        var type = req.Type?.ToLowerInvariant();
        if (type is not ("depense" or "revenu" or "transfert"))
            return "Type invalide : attendu depense, revenu ou transfert.";
        if (req.Montant <= 0)
            return "Le montant doit être strictement positif.";
        if (string.IsNullOrWhiteSpace(req.Libelle))
            return "Le libellé est obligatoire.";

        if (!await AppartientAsync(conn, tx, "comptes", req.CompteId, utilisateurId, ct))
            return "Compte introuvable.";

        if (type == "transfert")
        {
            if (req.CompteDestinationId is not Guid destination)
                return "Un transfert exige un compte de destination.";
            if (destination == req.CompteId)
                return "Le compte de destination doit différer du compte source.";
            if (!await AppartientAsync(conn, tx, "comptes", destination, utilisateurId, ct))
                return "Compte de destination introuvable.";
            return null;   // un transfert n'est ni une dépense ni un revenu : pas de catégorie
        }

        if (req.CategorieId is Guid categorie)
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT type::text FROM categories WHERE id = @id AND utilisateur_id = @uid", conn, tx);
            cmd.Ajouter("id", categorie);
            cmd.Ajouter("uid", utilisateurId);
            if (await cmd.ExecuteScalarAsync(ct) is not string typeCategorie)
                return "Catégorie introuvable.";
            if (typeCategorie != type)
                return $"La catégorie est de type « {typeCategorie} » et ne peut pas porter une opération de type « {type} ».";
        }

        return null;
    }

    private static async Task<bool> AppartientAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string table,
                                                    Guid id, Guid utilisateurId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT 1 FROM {table} WHERE id = @id AND utilisateur_id = @uid", conn, tx);
        cmd.Ajouter("id", id);
        cmd.Ajouter("uid", utilisateurId);
        return await cmd.ExecuteScalarAsync(ct) != null;
    }

    private static (string where, List<(string, object?)> parametres) ConstruireFiltre(
        Guid utilisateurId, FiltreTransactions f)
    {
        var conditions = new List<string> { "t.utilisateur_id = @uid" };
        var parametres = new List<(string, object?)> { ("uid", utilisateurId) };

        if (f.Debut is DateOnly debut)
        {
            conditions.Add("t.date_operation >= @debut");
            parametres.Add(("debut", debut));
        }
        if (f.Fin is DateOnly fin)
        {
            conditions.Add("t.date_operation <= @fin");
            parametres.Add(("fin", fin));
        }
        if (f.Type is "depense" or "revenu" or "transfert")
        {
            conditions.Add("t.type = @type::type_flux");
            parametres.Add(("type", f.Type));
        }
        if (f.CompteId is Guid compte)
        {
            // Un transfert concerne deux comptes : il doit ressortir des deux relevés.
            conditions.Add("(t.compte_id = @compte OR t.compte_destination_id = @compte)");
            parametres.Add(("compte", compte));
        }
        if (f.CategorieId is Guid categorie)
        {
            conditions.Add("t.categorie_id = @categorie");
            parametres.Add(("categorie", categorie));
        }
        if (f.MontantMin is decimal min)
        {
            conditions.Add("t.montant >= @min");
            parametres.Add(("min", min));
        }
        if (f.MontantMax is decimal max)
        {
            conditions.Add("t.montant <= @max");
            parametres.Add(("max", max));
        }
        if (f.Pointee is bool pointee)
        {
            conditions.Add("t.pointee = @pointee");
            parametres.Add(("pointee", pointee));
        }
        if (!string.IsNullOrWhiteSpace(f.Recherche))
        {
            conditions.Add("(t.libelle ILIKE @q OR t.tiers ILIKE @q OR t.note ILIKE @q)");
            parametres.Add(("q", $"%{f.Recherche.Trim()}%"));
        }

        return (string.Join(" AND ", conditions), parametres);
    }

    private static TransactionDto Lire(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        Type = r.GetString(1),
        Montant = r.Decimal(2),
        Devise = r.GetString(3).Trim(),
        DateOperation = r.GetFieldValue<DateOnly>(4),
        Libelle = r.GetString(5),
        Note = r.Texte(6),
        Tiers = r.Texte(7),
        ModePaiement = r.Texte(8),
        PieceJointeUrl = r.Texte(9),
        Pointee = r.GetBoolean(10),
        CompteId = r.GetGuid(11),
        CompteNom = r.GetString(12),
        CompteDestinationId = r.Uuid(13),
        CompteDestinationNom = r.Texte(14),
        CategorieId = r.Uuid(15),
        CategorieNom = r.Texte(16),
        CategorieIcone = r.Texte(17),
        CategorieCouleur = r.Texte(18),
        RecurrenceId = r.Uuid(19),
        DateCreation = r.Horodatage(20)
    };

    private static string? Vide(string? valeur) => string.IsNullOrWhiteSpace(valeur) ? null : valeur.Trim();
}
