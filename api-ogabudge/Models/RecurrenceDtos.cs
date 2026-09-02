using System.ComponentModel.DataAnnotations;

namespace OGABudget.Api.Models;

public class RecurrenceDto
{
    public Guid Id { get; set; }
    public string Libelle { get; set; } = "";
    public string? Note { get; set; }
    /// <summary>depense | revenu</summary>
    public string Type { get; set; } = "depense";
    public decimal Montant { get; set; }

    public Guid CompteId { get; set; }
    public string CompteNom { get; set; } = "";
    public Guid? CategorieId { get; set; }
    public string? CategorieNom { get; set; }

    /// <summary>quotidienne | hebdomadaire | mensuelle | trimestrielle | annuelle</summary>
    public string Frequence { get; set; } = "mensuelle";
    public int Intervalle { get; set; } = 1;
    public int? JourDuMois { get; set; }
    public DateOnly DateDebut { get; set; }
    public DateOnly? DateFin { get; set; }
    public DateOnly ProchaineEcheance { get; set; }
    public bool AutoGenerer { get; set; } = true;
    public bool Actif { get; set; } = true;
    public DateTimeOffset DateCreation { get; set; }
}

public class CreerRecurrenceRequest
{
    [Required, MaxLength(160)]
    public string Libelle { get; set; } = "";

    [MaxLength(1000)] public string? Note { get; set; }

    [Required, MaxLength(10)]
    public string Type { get; set; } = "depense";

    [Range(0.01, 999999999999.99)]
    public decimal Montant { get; set; }

    [Required] public Guid CompteId { get; set; }
    public Guid? CategorieId { get; set; }

    [MaxLength(15)] public string Frequence { get; set; } = "mensuelle";

    [Range(1, 52)] public int Intervalle { get; set; } = 1;

    [Range(1, 31)] public int? JourDuMois { get; set; }

    public DateOnly? DateDebut { get; set; }
    public DateOnly? DateFin { get; set; }
    public bool AutoGenerer { get; set; } = true;
}

public class MajRecurrenceRequest : CreerRecurrenceRequest
{
    public bool Actif { get; set; } = true;
}

/// <summary>Résultat de la matérialisation des échéances arrivées à terme.</summary>
public class GenerationRecurrencesDto
{
    public int TransactionsCreees { get; set; }
    public List<TransactionDto> Transactions { get; set; } = new();
}
