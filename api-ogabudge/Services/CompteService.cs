using Npgsql;
using OGABudget.Api.Infrastructure;
using OGABudget.Api.Models;

namespace OGABudget.Api.Services;

public class CompteService
{
    private readonly NpgsqlDataSource _db;

    public CompteService(NpgsqlDataSource db) => _db = db;

    /// <summary>Les soldes viennent de la vue <c>v_soldes_comptes</c> : jamais de colonne solde dénormalisée à resynchroniser.</summary>
    private const string Requete =
        """
        SELECT c.id, c.nom, c.type::text, c.institution, c.numero_masque, c.solde_initial,
               s.solde, c.devise::text, c.couleur, c.icone, c.inclus_dans_total, c.archive,
               c.ordre, s.nombre_operations, c.date_creation
        FROM comptes c
        JOIN v_soldes_comptes s ON s.compte_id = c.id
        """;

    public async Task<List<CompteDto>> ListerAsync(Guid utilisateurId, bool inclureArchives, CancellationToken ct)
    {
        var filtre = inclureArchives ? "" : " AND c.archive = false";
        await using var cmd = _db.CreateCommand(
            $"{Requete} WHERE c.utilisateur_id = @uid{filtre} ORDER BY c.archive, c.ordre, c.nom");
        cmd.Ajouter("uid", utilisateurId);

        var liste = new List<CompteDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) liste.Add(Lire(reader));
        return liste;
    }

    public async Task<CompteDto?> ObtenirAsync(Guid utilisateurId, Guid id, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand($"{Requete} WHERE c.utilisateur_id = @uid AND c.id = @id");
        cmd.Ajouter("uid", utilisateurId);
        cmd.Ajouter("id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Lire(reader) : null;
    }

    /// <returns>L'identifiant créé, ou <c>null</c> si un compte porte déjà ce nom.</returns>
    public async Task<Guid?> CreerAsync(Guid utilisateurId, CreerCompteRequest req, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand(
            """
            INSERT INTO comptes (utilisateur_id, nom, type, institution, numero_masque, solde_initial,
                                 devise, couleur, icone, inclus_dans_total, ordre)
            VALUES (@uid, @nom, @type::type_compte, @institution, @numero, @solde,
                    COALESCE(@devise, (SELECT devise FROM utilisateurs WHERE id = @uid)),
                    COALESCE(@couleur, '#1a2b5a'), COALESCE(@icone, 'wallet'), @inclus, @ordre)
            ON CONFLICT (utilisateur_id, nom) DO NOTHING
            RETURNING id
            """);
        cmd.Ajouter("uid", utilisateurId);
        cmd.Ajouter("nom", req.Nom.Trim());
        cmd.Ajouter("type", TypeValide(req.Type));
        cmd.Ajouter("institution", Vide(req.Institution));
        cmd.Ajouter("numero", Vide(req.NumeroMasque));
        cmd.Ajouter("solde", req.SoldeInitial);
        cmd.Ajouter("devise", Vide(req.Devise)?.ToUpperInvariant());
        cmd.Ajouter("couleur", Vide(req.Couleur));
        cmd.Ajouter("icone", Vide(req.Icone));
        cmd.Ajouter("inclus", req.InclusDansTotal);
        cmd.Ajouter("ordre", req.Ordre);

        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    public async Task<bool> MettreAJourAsync(Guid utilisateurId, Guid id, MajCompteRequest req, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand(
            """
            UPDATE comptes SET
                nom = @nom, type = @type::type_compte, institution = @institution,
                numero_masque = @numero, solde_initial = @solde,
                couleur = COALESCE(@couleur, couleur), icone = COALESCE(@icone, icone),
                inclus_dans_total = @inclus, archive = @archive, ordre = @ordre
            WHERE id = @id AND utilisateur_id = @uid
            """);
        cmd.Ajouter("id", id);
        cmd.Ajouter("uid", utilisateurId);
        cmd.Ajouter("nom", req.Nom.Trim());
        cmd.Ajouter("type", TypeValide(req.Type));
        cmd.Ajouter("institution", Vide(req.Institution));
        cmd.Ajouter("numero", Vide(req.NumeroMasque));
        cmd.Ajouter("solde", req.SoldeInitial);
        cmd.Ajouter("couleur", Vide(req.Couleur));
        cmd.Ajouter("icone", Vide(req.Icone));
        cmd.Ajouter("inclus", req.InclusDansTotal);
        cmd.Ajouter("archive", req.Archive);
        cmd.Ajouter("ordre", req.Ordre);

        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>
    /// Supprime le compte et, en cascade, ses transactions. Refuse tant que des opérations
    /// existent si <paramref name="forcer"/> est faux : l'archivage est presque toujours
    /// le bon geste, la suppression fait disparaître l'historique.
    /// </summary>
    public async Task<(bool supprime, int operations)> SupprimerAsync(Guid utilisateurId, Guid id, bool forcer,
                                                                     CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);

        int operations;
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT COUNT(*) FROM transactions t
            JOIN comptes c ON c.id = @id AND c.utilisateur_id = @uid
            WHERE t.compte_id = @id OR t.compte_destination_id = @id
            """, conn))
        {
            cmd.Ajouter("id", id);
            cmd.Ajouter("uid", utilisateurId);
            operations = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }

        if (operations > 0 && !forcer) return (false, operations);

        await using (var cmd = new NpgsqlCommand(
            "DELETE FROM comptes WHERE id = @id AND utilisateur_id = @uid", conn))
        {
            cmd.Ajouter("id", id);
            cmd.Ajouter("uid", utilisateurId);
            return (await cmd.ExecuteNonQueryAsync(ct) > 0, operations);
        }
    }

    /// <summary>Somme des comptes marqués « inclus dans le total », dans la devise de l'utilisateur.</summary>
    public async Task<decimal> SoldeTotalAsync(Guid utilisateurId, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand(
            """
            SELECT COALESCE(SUM(solde), 0) FROM v_soldes_comptes
            WHERE utilisateur_id = @uid AND inclus_dans_total = true AND archive = false
            """);
        cmd.Ajouter("uid", utilisateurId);
        return Convert.ToDecimal(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<bool> AppartientAsync(Guid utilisateurId, Guid compteId, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT 1 FROM comptes WHERE id = @id AND utilisateur_id = @uid");
        cmd.Ajouter("id", compteId);
        cmd.Ajouter("uid", utilisateurId);
        return await cmd.ExecuteScalarAsync(ct) != null;
    }

    private static CompteDto Lire(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        Nom = r.GetString(1),
        Type = r.GetString(2),
        Institution = r.Texte(3),
        NumeroMasque = r.Texte(4),
        SoldeInitial = r.Decimal(5),
        Solde = r.Decimal(6),
        Devise = r.GetString(7).Trim(),
        Couleur = r.GetString(8),
        Icone = r.GetString(9),
        InclusDansTotal = r.GetBoolean(10),
        Archive = r.GetBoolean(11),
        Ordre = r.Entier(12),
        NombreOperations = r.Entier(13),
        DateCreation = r.Horodatage(14)
    };

    private static readonly HashSet<string> TypesAutorises =
        new(StringComparer.OrdinalIgnoreCase)
        { "especes", "banque", "mobile_money", "epargne", "carte", "credit", "autre" };

    private static string TypeValide(string? type) =>
        type != null && TypesAutorises.Contains(type) ? type.ToLowerInvariant() : "especes";

    private static string? Vide(string? valeur) => string.IsNullOrWhiteSpace(valeur) ? null : valeur.Trim();
}
