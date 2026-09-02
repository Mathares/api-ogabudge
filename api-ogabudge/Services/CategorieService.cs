using Npgsql;
using OGABudget.Api.Infrastructure;
using OGABudget.Api.Models;

namespace OGABudget.Api.Services;

public class CategorieService
{
    private readonly NpgsqlDataSource _db;

    public CategorieService(NpgsqlDataSource db) => _db = db;

    private const string Colonnes =
        "id, parent_id, nom, type::text, icone, couleur, systeme, archive, ordre";

    /// <param name="type">« depense », « revenu », ou null pour les deux.</param>
    /// <param name="arborescence">Vrai pour imbriquer les sous-catégories dans leur parent.</param>
    public async Task<List<CategorieDto>> ListerAsync(Guid utilisateurId, string? type, bool inclureArchivees,
                                                      bool arborescence, CancellationToken ct)
    {
        var conditions = new List<string> { "utilisateur_id = @uid" };
        if (!inclureArchivees) conditions.Add("archive = false");
        if (type is "depense" or "revenu") conditions.Add("type = @type::type_flux");

        await using var cmd = _db.CreateCommand(
            $"SELECT {Colonnes} FROM categories WHERE {string.Join(" AND ", conditions)} ORDER BY type, ordre, nom");
        cmd.Ajouter("uid", utilisateurId);
        if (type is "depense" or "revenu") cmd.Ajouter("type", type);

        var plates = new List<CategorieDto>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) plates.Add(Lire(reader));

        if (!arborescence) return plates;

        var parIdentifiant = plates.ToDictionary(c => c.Id);
        var racines = new List<CategorieDto>();
        foreach (var categorie in plates)
        {
            if (categorie.ParentId is Guid parent && parIdentifiant.TryGetValue(parent, out var pere))
                pere.Enfants.Add(categorie);
            else
                racines.Add(categorie);
        }
        return racines;
    }

    public async Task<CategorieDto?> ObtenirAsync(Guid utilisateurId, Guid id, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand(
            $"SELECT {Colonnes} FROM categories WHERE id = @id AND utilisateur_id = @uid");
        cmd.Ajouter("id", id);
        cmd.Ajouter("uid", utilisateurId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Lire(reader) : null;
    }

    public async Task<Guid?> CreerAsync(Guid utilisateurId, CreerCategorieRequest req, CancellationToken ct)
    {
        var type = req.Type?.ToLowerInvariant();
        if (type is not ("depense" or "revenu")) return null;

        // Une sous-catégorie hérite forcément du type de son parent, sinon les
        // agrégations mélangeraient dépenses et revenus dans la même branche.
        if (req.ParentId is Guid parent)
        {
            await using var verif = _db.CreateCommand(
                "SELECT type::text FROM categories WHERE id = @id AND utilisateur_id = @uid");
            verif.Ajouter("id", parent);
            verif.Ajouter("uid", utilisateurId);
            if (await verif.ExecuteScalarAsync(ct) is not string typeParent || typeParent != type) return null;
        }

        await using var cmd = _db.CreateCommand(
            """
            INSERT INTO categories (utilisateur_id, parent_id, nom, type, icone, couleur, ordre)
            VALUES (@uid, @parent, @nom, @type::type_flux,
                    COALESCE(@icone, 'tag'), COALESCE(@couleur, '#085041'), @ordre)
            ON CONFLICT DO NOTHING
            RETURNING id
            """);
        cmd.Ajouter("uid", utilisateurId);
        cmd.Ajouter("parent", req.ParentId);
        cmd.Ajouter("nom", req.Nom.Trim());
        cmd.Ajouter("type", type);
        cmd.Ajouter("icone", Vide(req.Icone));
        cmd.Ajouter("couleur", Vide(req.Couleur));
        cmd.Ajouter("ordre", req.Ordre);

        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    public async Task<bool> MettreAJourAsync(Guid utilisateurId, Guid id, MajCategorieRequest req, CancellationToken ct)
    {
        if (req.ParentId == id) return false;   // une catégorie ne peut pas être son propre parent

        await using var cmd = _db.CreateCommand(
            """
            UPDATE categories SET
                nom = @nom, parent_id = @parent,
                icone = COALESCE(@icone, icone), couleur = COALESCE(@couleur, couleur),
                archive = @archive, ordre = @ordre
            WHERE id = @id AND utilisateur_id = @uid
            """);
        cmd.Ajouter("id", id);
        cmd.Ajouter("uid", utilisateurId);
        cmd.Ajouter("nom", req.Nom.Trim());
        cmd.Ajouter("parent", req.ParentId);
        cmd.Ajouter("icone", Vide(req.Icone));
        cmd.Ajouter("couleur", Vide(req.Couleur));
        cmd.Ajouter("archive", req.Archive);
        cmd.Ajouter("ordre", req.Ordre);

        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>
    /// Supprime une catégorie. Les transactions rattachées ne sont pas détruites :
    /// leur <c>categorie_id</c> passe à NULL (« Sans catégorie »).
    /// </summary>
    /// <returns>« utilisee » indique le nombre de transactions qui perdraient leur catégorie.</returns>
    public async Task<(bool supprimee, int utilisee, bool systeme)> SupprimerAsync(
        Guid utilisateurId, Guid id, bool forcer, CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);

        bool systeme;
        await using (var cmd = new NpgsqlCommand(
            "SELECT systeme FROM categories WHERE id = @id AND utilisateur_id = @uid", conn))
        {
            cmd.Ajouter("id", id);
            cmd.Ajouter("uid", utilisateurId);
            if (await cmd.ExecuteScalarAsync(ct) is not bool s) return (false, 0, false);
            systeme = s;
        }

        int utilisee;
        await using (var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM transactions WHERE categorie_id = @id", conn))
        {
            cmd.Ajouter("id", id);
            utilisee = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }

        if (utilisee > 0 && !forcer) return (false, utilisee, systeme);

        await using (var cmd = new NpgsqlCommand(
            "DELETE FROM categories WHERE id = @id AND utilisateur_id = @uid", conn))
        {
            cmd.Ajouter("id", id);
            cmd.Ajouter("uid", utilisateurId);
            return (await cmd.ExecuteNonQueryAsync(ct) > 0, utilisee, systeme);
        }
    }

    /// <summary>Vérifie l'appartenance et le type avant d'accepter une transaction.</summary>
    public async Task<string?> TypeDeAsync(Guid utilisateurId, Guid categorieId, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT type::text FROM categories WHERE id = @id AND utilisateur_id = @uid");
        cmd.Ajouter("id", categorieId);
        cmd.Ajouter("uid", utilisateurId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    private static CategorieDto Lire(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        ParentId = r.Uuid(1),
        Nom = r.GetString(2),
        Type = r.GetString(3),
        Icone = r.GetString(4),
        Couleur = r.GetString(5),
        Systeme = r.GetBoolean(6),
        Archive = r.GetBoolean(7),
        Ordre = r.Entier(8)
    };

    private static string? Vide(string? valeur) => string.IsNullOrWhiteSpace(valeur) ? null : valeur.Trim();
}
