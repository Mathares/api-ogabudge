using System.ComponentModel.DataAnnotations;

namespace OGABudget.Api.Models;

public class BudgetDto
{
    public Guid Id { get; set; }
    public string Nom { get; set; } = "";
    public Guid? CategorieId { get; set; }
    public string? CategorieNom { get; set; }
    public string? CategorieIcone { get; set; }
    public string? CategorieCouleur { get; set; }
    public decimal MontantPlafond { get; set; }
    /// <summary>hebdomadaire | mensuelle | trimestrielle | annuelle</summary>
    public string Periode { get; set; } = "mensuelle";
    public DateOnly DateDebut { get; set; }
    public DateOnly? DateFin { get; set; }
    public int SeuilAlerte { get; set; } = 80;
    public bool ReportSolde { get; set; }
    public bool Actif { get; set; } = true;

    // ─── État calculé sur la période en cours ───
    public DateOnly PeriodeDebut { get; set; }
    public DateOnly PeriodeFin { get; set; }
    public decimal MontantConsomme { get; set; }
    public decimal MontantRestant { get; set; }
    /// <summary>Consommation en % du plafond (peut dépasser 100).</summary>
    public decimal PourcentageConsomme { get; set; }
    public bool AlerteAtteinte { get; set; }
    public bool Depasse { get; set; }
    /// <summary>Montant journalier restant possible jusqu'à la fin de la période.</summary>
    public decimal RythmeJournalierRestant { get; set; }
}

public class CreerBudgetRequest
{
    [Required, MaxLength(80)]
    public string Nom { get; set; } = "";

    /// <summary>Null pour un budget global (toutes dépenses confondues).</summary>
    public Guid? CategorieId { get; set; }

    [Range(0.01, 999999999999.99)]
    public decimal MontantPlafond { get; set; }

    [MaxLength(15)]
    public string Periode { get; set; } = "mensuelle";

    public DateOnly? DateDebut { get; set; }
    public DateOnly? DateFin { get; set; }

    [Range(1, 100)]
    public int SeuilAlerte { get; set; } = 80;

    public bool ReportSolde { get; set; }
}

public class MajBudgetRequest : CreerBudgetRequest
{
    public bool Actif { get; set; } = true;
}
