namespace OGABudget.Api.Models;

/// <summary>Totaux d'une période : le bloc chiffré en haut de l'écran d'accueil.</summary>
public class ResumePeriodeDto
{
    public DateOnly Debut { get; set; }
    public DateOnly Fin { get; set; }
    public decimal TotalRevenus { get; set; }
    public decimal TotalDepenses { get; set; }
    public decimal SoldeNet => TotalRevenus - TotalDepenses;
    /// <summary>Part du revenu non dépensée, en %. 0 si aucun revenu sur la période.</summary>
    public decimal TauxEpargne { get; set; }
    public int NombreTransactions { get; set; }
    public decimal DepenseMoyenneJournaliere { get; set; }
    public string Devise { get; set; } = "XOF";
}

public class LigneCategorieDto
{
    public Guid? CategorieId { get; set; }
    public string CategorieNom { get; set; } = "Sans catégorie";
    public string Icone { get; set; } = "tag";
    public string Couleur { get; set; } = "#adb5bd";
    public decimal Montant { get; set; }
    public decimal Pourcentage { get; set; }
    public int NombreTransactions { get; set; }
}

public class PointEvolutionDto
{
    /// <summary>Premier jour de la période agrégée.</summary>
    public DateOnly Periode { get; set; }
    public string Libelle { get; set; } = "";
    public decimal Revenus { get; set; }
    public decimal Depenses { get; set; }
    public decimal Solde => Revenus - Depenses;
}

public class LigneCompteDto
{
    public Guid CompteId { get; set; }
    public string Nom { get; set; } = "";
    public string Type { get; set; } = "";
    public string Devise { get; set; } = "XOF";
    public decimal Solde { get; set; }
}

/// <summary>Charge unique de l'écran d'accueil : évite 6 appels au démarrage du mobile.</summary>
public class TableauDeBordDto
{
    public decimal SoldeTotal { get; set; }
    public string Devise { get; set; } = "XOF";
    public ResumePeriodeDto MoisEnCours { get; set; } = new();
    public ResumePeriodeDto MoisPrecedent { get; set; } = new();
    /// <summary>Variation des dépenses par rapport au mois précédent, en %.</summary>
    public decimal VariationDepenses { get; set; }
    public List<LigneCompteDto> Comptes { get; set; } = new();
    public List<LigneCategorieDto> TopDepenses { get; set; } = new();
    public List<BudgetDto> BudgetsEnAlerte { get; set; } = new();
    public List<ObjectifDto> ObjectifsEnCours { get; set; } = new();
    public List<TransactionDto> DernieresTransactions { get; set; } = new();
    public List<RecurrenceDto> EcheancesAVenir { get; set; } = new();
}
