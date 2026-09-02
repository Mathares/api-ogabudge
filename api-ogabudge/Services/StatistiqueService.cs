using Npgsql;
using OGABudget.Api.Infrastructure;
using OGABudget.Api.Models;

namespace OGABudget.Api.Services;

/// <summary>
/// Agrégations pour les écrans d'analyse. Les transferts sont systématiquement exclus :
/// déplacer de l'argent entre ses propres comptes n'est ni une dépense ni un revenu.
/// </summary>
public class StatistiqueService
{
    private readonly NpgsqlDataSource _db;
    private readonly CompteService _comptes;
    private readonly BudgetService _budgets;
    private readonly ObjectifService _objectifs;
    private readonly TransactionService _transactions;
    private readonly RecurrenceService _recurrences;

    public StatistiqueService(NpgsqlDataSource db, CompteService comptes, BudgetService budgets,
                              ObjectifService objectifs, TransactionService transactions,
                              RecurrenceService recurrences)
    {
        _db = db;
        _comptes = comptes;
        _budgets = budgets;
        _objectifs = objectifs;
        _transactions = transactions;
        _recurrences = recurrences;
    }

    // ─── Résumé d'une période ───────────────────────────────────────────────

    public async Task<ResumePeriodeDto> ResumeAsync(Guid utilisateurId, DateOnly debut, DateOnly fin,
                                                    CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await ResumeAsync(conn, utilisateurId, debut, fin, ct);
    }

    private static async Task<ResumePeriodeDto> ResumeAsync(NpgsqlConnection conn, Guid utilisateurId,
                                                            DateOnly debut, DateOnly fin, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT COALESCE(SUM(montant) FILTER (WHERE type = 'revenu'), 0),
                   COALESCE(SUM(montant) FILTER (WHERE type = 'depense'), 0),
                   COUNT(*) FILTER (WHERE type <> 'transfert'),
                   (SELECT devise FROM utilisateurs WHERE id = @uid)
            FROM transactions
            WHERE utilisateur_id = @uid AND date_operation BETWEEN @debut AND @fin
            """, conn);
        cmd.Ajouter("uid", utilisateurId);
        cmd.Ajouter("debut", debut);
        cmd.Ajouter("fin", fin);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        var revenus = reader.Decimal(0);
        var depenses = reader.Decimal(1);
        var jours = Math.Max(1, fin.DayNumber - debut.DayNumber + 1);

        return new ResumePeriodeDto
        {
            Debut = debut,
            Fin = fin,
            TotalRevenus = revenus,
            TotalDepenses = depenses,
            TauxEpargne = revenus == 0 ? 0 : Math.Round((revenus - depenses) * 100m / revenus, 1),
            NombreTransactions = reader.Entier(2),
            DepenseMoyenneJournaliere = Math.Round(depenses / jours, 2),
            Devise = reader.Texte(3)?.Trim() ?? "XOF"
        };
    }

    // ─── Répartition par catégorie ──────────────────────────────────────────

    /// <param name="type">« depense » (défaut) ou « revenu ».</param>
    public async Task<List<LigneCategorieDto>> ParCategorieAsync(Guid utilisateurId, DateOnly debut, DateOnly fin,
                                                                 string type, CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await ParCategorieAsync(conn, utilisateurId, debut, fin, type, null, ct);
    }

    private static async Task<List<LigneCategorieDto>> ParCategorieAsync(
        NpgsqlConnection conn, Guid utilisateurId, DateOnly debut, DateOnly fin, string type, int? limite,
        CancellationToken ct)
    {
        if (type is not ("depense" or "revenu")) type = "depense";

        // Les sous-catégories remontent sur leur parent : l'utilisateur veut voir
        // « Transport », pas « Transport / Carburant » et « Transport / Taxi » séparément.
        await using var cmd = new NpgsqlCommand(
            $"""
             SELECT racine.id, racine.nom, racine.icone, racine.couleur,
                    SUM(t.montant), COUNT(*)
             FROM transactions t
             LEFT JOIN categories cat ON cat.id = t.categorie_id
             LEFT JOIN categories racine ON racine.id = COALESCE(cat.parent_id, cat.id)
             WHERE t.utilisateur_id = @uid AND t.type = @type::type_flux
               AND t.date_operation BETWEEN @debut AND @fin
             GROUP BY racine.id, racine.nom, racine.icone, racine.couleur
             ORDER BY SUM(t.montant) DESC
             {(limite is int n ? $"LIMIT {n}" : "")}
             """, conn);
        cmd.Ajouter("uid", utilisateurId);
        cmd.Ajouter("type", type);
        cmd.Ajouter("debut", debut);
        cmd.Ajouter("fin", fin);

        var lignes = new List<LigneCategorieDto>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                lignes.Add(new LigneCategorieDto
                {
                    CategorieId = reader.Uuid(0),
                    CategorieNom = reader.Texte(1) ?? "Sans catégorie",
                    Icone = reader.Texte(2) ?? "tag",
                    Couleur = reader.Texte(3) ?? "#adb5bd",
                    Montant = reader.Decimal(4),
                    NombreTransactions = reader.Entier(5)
                });

        var total = lignes.Sum(l => l.Montant);
        if (total > 0)
            foreach (var ligne in lignes)
                ligne.Pourcentage = Math.Round(ligne.Montant * 100m / total, 1);

        return lignes;
    }

    // ─── Courbe d'évolution ─────────────────────────────────────────────────

    /// <param name="granularite">jour | semaine | mois | annee</param>
    public async Task<List<PointEvolutionDto>> EvolutionAsync(Guid utilisateurId, DateOnly debut, DateOnly fin,
                                                              string granularite, CancellationToken ct)
    {
        var champ = granularite switch
        {
            "jour"    => "date_operation",
            "semaine" => "date_trunc('week', date_operation)::date",
            "annee"   => "date_trunc('year', date_operation)::date",
            _         => "date_trunc('month', date_operation)::date",
        };

        await using var cmd = _db.CreateCommand(
            $"""
             SELECT {champ} AS periode,
                    COALESCE(SUM(montant) FILTER (WHERE type = 'revenu'), 0),
                    COALESCE(SUM(montant) FILTER (WHERE type = 'depense'), 0)
             FROM transactions
             WHERE utilisateur_id = @uid AND date_operation BETWEEN @debut AND @fin
               AND type <> 'transfert'
             GROUP BY periode
             ORDER BY periode
             """);
        cmd.Ajouter("uid", utilisateurId);
        cmd.Ajouter("debut", debut);
        cmd.Ajouter("fin", fin);

        var mesures = new Dictionary<DateOnly, (decimal revenus, decimal depenses)>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                mesures[reader.GetFieldValue<DateOnly>(0)] = (reader.Decimal(1), reader.Decimal(2));

        // Les périodes sans mouvement doivent apparaître à zéro, sinon la courbe ment.
        return Periodes.Decouper(debut, fin, granularite)
            .Select(tranche =>
            {
                mesures.TryGetValue(tranche.debut, out var valeurs);
                return new PointEvolutionDto
                {
                    Periode = tranche.debut,
                    Libelle = tranche.libelle,
                    Revenus = valeurs.revenus,
                    Depenses = valeurs.depenses
                };
            })
            .ToList();
    }

    // ─── Tableau de bord ────────────────────────────────────────────────────

    /// <summary>Écran d'accueil complet en un appel : le mobile n'ouvre qu'une requête au démarrage.</summary>
    public async Task<TableauDeBordDto> TableauDeBordAsync(Guid utilisateurId, DateOnly? reference,
                                                           CancellationToken ct)
    {
        var jour = reference ?? DateOnly.FromDateTime(DateTime.UtcNow);

        await using var conn = await _db.OpenConnectionAsync(ct);

        int jourDebutMois;
        string devise;
        await using (var cmd = new NpgsqlCommand(
            "SELECT jour_debut_mois, devise::text FROM utilisateurs WHERE id = @uid", conn))
        {
            cmd.Ajouter("uid", utilisateurId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return new TableauDeBordDto();
            jourDebutMois = reader.Entier(0);
            devise = reader.GetString(1).Trim();
        }

        var (debutMois, finMois) = Periodes.MoisBudgetaire(jour, jourDebutMois);
        var (debutPrecedent, finPrecedent) = Periodes.MoisBudgetaire(debutMois.AddDays(-1), jourDebutMois);

        var moisEnCours = await ResumeAsync(conn, utilisateurId, debutMois, finMois, ct);
        var moisPrecedent = await ResumeAsync(conn, utilisateurId, debutPrecedent, finPrecedent, ct);
        var topDepenses = await ParCategorieAsync(conn, utilisateurId, debutMois, finMois, "depense", 5, ct);

        var comptes = new List<LigneCompteDto>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT compte_id, nom, type::text, devise::text, solde
            FROM v_soldes_comptes
            WHERE utilisateur_id = @uid AND archive = false
            ORDER BY solde DESC
            """, conn))
        {
            cmd.Ajouter("uid", utilisateurId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                comptes.Add(new LigneCompteDto
                {
                    CompteId = reader.GetGuid(0),
                    Nom = reader.GetString(1),
                    Type = reader.GetString(2),
                    Devise = reader.GetString(3).Trim(),
                    Solde = reader.Decimal(4)
                });
        }

        var dernieres = await _transactions.ListerAsync(utilisateurId,
            new FiltreTransactions { Page = 1, TaillePage = 10 }, ct);

        return new TableauDeBordDto
        {
            SoldeTotal = await _comptes.SoldeTotalAsync(utilisateurId, ct),
            Devise = devise,
            MoisEnCours = moisEnCours,
            MoisPrecedent = moisPrecedent,
            VariationDepenses = moisPrecedent.TotalDepenses == 0
                ? 0
                : Math.Round((moisEnCours.TotalDepenses - moisPrecedent.TotalDepenses) * 100m
                             / moisPrecedent.TotalDepenses, 1),
            Comptes = comptes,
            TopDepenses = topDepenses,
            BudgetsEnAlerte = await _budgets.EnAlerteAsync(utilisateurId, ct),
            ObjectifsEnCours = (await _objectifs.ListerAsync(utilisateurId, "en_cours", ct)).Take(5).ToList(),
            DernieresTransactions = dernieres.Elements,
            EcheancesAVenir = await _recurrences.ProchainesAsync(utilisateurId, 7, ct)
        };
    }
}
