using Npgsql;
using OGABudget.Api.Infrastructure;
using OGABudget.Api.Models;

namespace OGABudget.Api.Services;

/// <summary>
/// Enveloppes budgétaires. Le montant consommé n'est jamais stocké : il est recalculé
/// depuis les transactions de la période courante, donc toujours juste après une
/// correction de saisie.
/// </summary>
public class BudgetService
{
    private readonly NpgsqlDataSource _db;

    public BudgetService(NpgsqlDataSource db) => _db = db;

    private const string Selection =
        """
        SELECT b.id, b.nom, b.categorie_id, cat.nom, cat.icone, cat.couleur,
               b.montant_plafond, b.periode::text, b.date_debut, b.date_fin,
               b.seuil_alerte, b.report_solde, b.actif
        FROM budgets b
        LEFT JOIN categories cat ON cat.id = b.categorie_id
        """;

    public async Task<List<BudgetDto>> ListerAsync(Guid utilisateurId, bool inclureInactifs, DateOnly? reference,
                                                   CancellationToken ct)
    {
        var jour = reference ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var filtre = inclureInactifs ? "" : " AND b.actif = true";

        await using var conn = await _db.OpenConnectionAsync(ct);

        var budgets = new List<BudgetDto>();
        await using (var cmd = new NpgsqlCommand(
            $"{Selection} WHERE b.utilisateur_id = @uid{filtre} ORDER BY b.actif DESC, b.nom", conn))
        {
            cmd.Ajouter("uid", utilisateurId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) budgets.Add(Lire(reader));
        }

        foreach (var budget in budgets)
            await CalculerConsommationAsync(conn, utilisateurId, budget, jour, ct);

        return budgets;
    }

    public async Task<BudgetDto?> ObtenirAsync(Guid utilisateurId, Guid id, DateOnly? reference, CancellationToken ct)
    {
        var jour = reference ?? DateOnly.FromDateTime(DateTime.UtcNow);

        await using var conn = await _db.OpenConnectionAsync(ct);
        BudgetDto budget;
        await using (var cmd = new NpgsqlCommand(
            $"{Selection} WHERE b.id = @id AND b.utilisateur_id = @uid", conn))
        {
            cmd.Ajouter("id", id);
            cmd.Ajouter("uid", utilisateurId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            budget = Lire(reader);
        }

        await CalculerConsommationAsync(conn, utilisateurId, budget, jour, ct);
        return budget;
    }

    /// <summary>Budgets ayant franchi leur seuil d'alerte sur la période en cours.</summary>
    public async Task<List<BudgetDto>> EnAlerteAsync(Guid utilisateurId, CancellationToken ct)
    {
        var budgets = await ListerAsync(utilisateurId, false, null, ct);
        return budgets.Where(b => b.AlerteAtteinte).OrderByDescending(b => b.PourcentageConsomme).ToList();
    }

    public async Task<(Guid? id, string? erreur)> CreerAsync(Guid utilisateurId, CreerBudgetRequest req,
                                                             CancellationToken ct)
    {
        var periode = PeriodeValide(req.Periode);
        if (periode == null) return (null, "Période invalide : hebdomadaire, mensuelle, trimestrielle ou annuelle.");
        if (req.MontantPlafond <= 0) return (null, "Le plafond doit être strictement positif.");

        await using var conn = await _db.OpenConnectionAsync(ct);

        if (req.CategorieId is Guid categorie)
        {
            await using var verif = new NpgsqlCommand(
                "SELECT type::text FROM categories WHERE id = @id AND utilisateur_id = @uid", conn);
            verif.Ajouter("id", categorie);
            verif.Ajouter("uid", utilisateurId);
            if (await verif.ExecuteScalarAsync(ct) is not string type) return (null, "Catégorie introuvable.");
            if (type != "depense") return (null, "Un budget ne s'applique qu'à une catégorie de dépense.");
        }

        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO budgets (utilisateur_id, categorie_id, nom, montant_plafond, periode,
                                 date_debut, date_fin, seuil_alerte, report_solde)
            VALUES (@uid, @categorie, @nom, @plafond, @periode::periode_budget,
                    @debut, @fin, @seuil, @report)
            ON CONFLICT DO NOTHING
            RETURNING id
            """, conn);
        cmd.Ajouter("uid", utilisateurId);
        cmd.Ajouter("categorie", req.CategorieId);
        cmd.Ajouter("nom", req.Nom.Trim());
        cmd.Ajouter("plafond", req.MontantPlafond);
        cmd.Ajouter("periode", periode);
        cmd.Ajouter("debut", req.DateDebut ?? DebutParDefaut(periode));
        cmd.Ajouter("fin", req.DateFin);
        cmd.Ajouter("seuil", (short)Math.Clamp(req.SeuilAlerte, 1, 100));
        cmd.Ajouter("report", req.ReportSolde);

        if (await cmd.ExecuteScalarAsync(ct) is not Guid id)
            return (null, "Un budget actif existe déjà pour cette catégorie et cette période.");
        return (id, null);
    }

    public async Task<(bool ok, string? erreur)> MettreAJourAsync(Guid utilisateurId, Guid id, MajBudgetRequest req,
                                                                  CancellationToken ct)
    {
        var periode = PeriodeValide(req.Periode);
        if (periode == null) return (false, "Période invalide.");
        if (req.MontantPlafond <= 0) return (false, "Le plafond doit être strictement positif.");

        await using var cmd = _db.CreateCommand(
            """
            UPDATE budgets SET
                nom = @nom, categorie_id = @categorie, montant_plafond = @plafond,
                periode = @periode::periode_budget, date_debut = COALESCE(@debut, date_debut),
                date_fin = @fin, seuil_alerte = @seuil, report_solde = @report, actif = @actif
            WHERE id = @id AND utilisateur_id = @uid
            """);
        cmd.Ajouter("id", id);
        cmd.Ajouter("uid", utilisateurId);
        cmd.Ajouter("nom", req.Nom.Trim());
        cmd.Ajouter("categorie", req.CategorieId);
        cmd.Ajouter("plafond", req.MontantPlafond);
        cmd.Ajouter("periode", periode);
        cmd.Ajouter("debut", req.DateDebut);
        cmd.Ajouter("fin", req.DateFin);
        cmd.Ajouter("seuil", (short)Math.Clamp(req.SeuilAlerte, 1, 100));
        cmd.Ajouter("report", req.ReportSolde);
        cmd.Ajouter("actif", req.Actif);

        return (await cmd.ExecuteNonQueryAsync(ct) > 0, null);
    }

    public async Task<bool> SupprimerAsync(Guid utilisateurId, Guid id, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("DELETE FROM budgets WHERE id = @id AND utilisateur_id = @uid");
        cmd.Ajouter("id", id);
        cmd.Ajouter("uid", utilisateurId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    // ─── Interne ────────────────────────────────────────────────────────────

    private static async Task CalculerConsommationAsync(NpgsqlConnection conn, Guid utilisateurId, BudgetDto budget,
                                                        DateOnly reference, CancellationToken ct)
    {
        var (debut, fin) = Periodes.FenetreBudget(budget.Periode, budget.DateDebut, reference);
        if (budget.DateFin is DateOnly limite && fin > limite) fin = limite;

        budget.PeriodeDebut = debut;
        budget.PeriodeFin = fin;

        // Les transferts sont exclus : déplacer de l'argent entre ses propres comptes
        // n'appauvrit pas l'utilisateur et ne doit pas consommer une enveloppe.
        var filtreCategorie = budget.CategorieId is null
            ? ""
            : " AND (t.categorie_id = @categorie OR t.categorie_id IN " +
              "(SELECT id FROM categories WHERE parent_id = @categorie))";

        await using var cmd = new NpgsqlCommand(
            $"""
             SELECT COALESCE(SUM(t.montant), 0)
             FROM transactions t
             WHERE t.utilisateur_id = @uid AND t.type = 'depense'
               AND t.date_operation BETWEEN @debut AND @fin{filtreCategorie}
             """, conn);
        cmd.Ajouter("uid", utilisateurId);
        cmd.Ajouter("debut", debut);
        cmd.Ajouter("fin", fin);
        if (budget.CategorieId is Guid categorie) cmd.Ajouter("categorie", categorie);

        budget.MontantConsomme = Convert.ToDecimal(await cmd.ExecuteScalarAsync(ct));
        budget.MontantRestant = budget.MontantPlafond - budget.MontantConsomme;
        budget.PourcentageConsomme = budget.MontantPlafond == 0
            ? 0
            : Math.Round(budget.MontantConsomme * 100m / budget.MontantPlafond, 1);
        budget.Depasse = budget.MontantConsomme > budget.MontantPlafond;
        budget.AlerteAtteinte = budget.PourcentageConsomme >= budget.SeuilAlerte;

        var joursRestants = Math.Max(1, fin.DayNumber - reference.DayNumber + 1);
        budget.RythmeJournalierRestant = budget.MontantRestant <= 0
            ? 0
            : Math.Round(budget.MontantRestant / joursRestants, 2);
    }

    private static BudgetDto Lire(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        Nom = r.GetString(1),
        CategorieId = r.Uuid(2),
        CategorieNom = r.Texte(3),
        CategorieIcone = r.Texte(4),
        CategorieCouleur = r.Texte(5),
        MontantPlafond = r.Decimal(6),
        Periode = r.GetString(7),
        DateDebut = r.GetFieldValue<DateOnly>(8),
        DateFin = r.DateNullable(9),
        SeuilAlerte = r.Entier(10),
        ReportSolde = r.GetBoolean(11),
        Actif = r.GetBoolean(12)
    };

    private static string? PeriodeValide(string? periode) => periode?.ToLowerInvariant() switch
    {
        "hebdomadaire" or "mensuelle" or "trimestrielle" or "annuelle" => periode!.ToLowerInvariant(),
        _ => null
    };

    /// <summary>Sans date fournie, le budget démarre au début de la période courante.</summary>
    private static DateOnly DebutParDefaut(string periode)
    {
        var aujourdhui = DateOnly.FromDateTime(DateTime.UtcNow);
        return periode switch
        {
            "hebdomadaire"  => aujourdhui.AddDays(-((int)aujourdhui.DayOfWeek + 6) % 7),
            "trimestrielle" => new DateOnly(aujourdhui.Year, ((aujourdhui.Month - 1) / 3) * 3 + 1, 1),
            "annuelle"      => new DateOnly(aujourdhui.Year, 1, 1),
            _               => new DateOnly(aujourdhui.Year, aujourdhui.Month, 1),
        };
    }
}
