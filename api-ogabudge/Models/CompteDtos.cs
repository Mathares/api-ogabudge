using System.ComponentModel.DataAnnotations;

namespace OGABudget.Api.Models;

public class CompteDto
{
    public Guid Id { get; set; }
    public string Nom { get; set; } = "";
    public string Type { get; set; } = "especes";
    public string? Institution { get; set; }
    public string? NumeroMasque { get; set; }
    public decimal SoldeInitial { get; set; }
    /// <summary>Solde courant = solde initial + revenus + transferts entrants − dépenses − transferts sortants.</summary>
    public decimal Solde { get; set; }
    public string Devise { get; set; } = "XOF";
    public string Couleur { get; set; } = "#1a2b5a";
    public string Icone { get; set; } = "wallet";
    public bool InclusDansTotal { get; set; } = true;
    public bool Archive { get; set; }
    public int Ordre { get; set; }
    public int NombreOperations { get; set; }
    public DateTimeOffset DateCreation { get; set; }
}

public class CreerCompteRequest
{
    [Required, MaxLength(80)]
    public string Nom { get; set; } = "";

    /// <summary>especes | banque | mobile_money | epargne | carte | credit | autre</summary>
    [MaxLength(20)]
    public string Type { get; set; } = "especes";

    [MaxLength(80)]  public string? Institution { get; set; }
    [MaxLength(10)]  public string? NumeroMasque { get; set; }
    public decimal SoldeInitial { get; set; }
    [MaxLength(3)]   public string? Devise { get; set; }
    [MaxLength(9)]   public string? Couleur { get; set; }
    [MaxLength(40)]  public string? Icone { get; set; }
    public bool InclusDansTotal { get; set; } = true;
    public int Ordre { get; set; }
}

public class MajCompteRequest
{
    [Required, MaxLength(80)]
    public string Nom { get; set; } = "";

    [MaxLength(20)] public string Type { get; set; } = "especes";
    [MaxLength(80)] public string? Institution { get; set; }
    [MaxLength(10)] public string? NumeroMasque { get; set; }
    public decimal SoldeInitial { get; set; }
    [MaxLength(9)]  public string? Couleur { get; set; }
    [MaxLength(40)] public string? Icone { get; set; }
    public bool InclusDansTotal { get; set; } = true;
    public bool Archive { get; set; }
    public int Ordre { get; set; }
}
