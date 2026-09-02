using System.ComponentModel.DataAnnotations;

namespace OGABudget.Api.Models;

public class TransactionDto
{
    public Guid Id { get; set; }
    /// <summary>depense | revenu | transfert</summary>
    public string Type { get; set; } = "depense";
    public decimal Montant { get; set; }
    public string Devise { get; set; } = "XOF";
    public DateOnly DateOperation { get; set; }
    public string Libelle { get; set; } = "";
    public string? Note { get; set; }
    public string? Tiers { get; set; }
    public string? ModePaiement { get; set; }
    public string? PieceJointeUrl { get; set; }
    public bool Pointee { get; set; }

    public Guid CompteId { get; set; }
    public string CompteNom { get; set; } = "";
    public Guid? CompteDestinationId { get; set; }
    public string? CompteDestinationNom { get; set; }

    public Guid? CategorieId { get; set; }
    public string? CategorieNom { get; set; }
    public string? CategorieIcone { get; set; }
    public string? CategorieCouleur { get; set; }

    public Guid? RecurrenceId { get; set; }
    public DateTimeOffset DateCreation { get; set; }
}

public class CreerTransactionRequest
{
    [Required, MaxLength(12)]
    public string Type { get; set; } = "depense";

    [Range(0.01, 999999999999.99)]
    public decimal Montant { get; set; }

    [Required]
    public Guid CompteId { get; set; }

    /// <summary>Obligatoire et unique champ compte supplémentaire pour <c>type = transfert</c>.</summary>
    public Guid? CompteDestinationId { get; set; }

    /// <summary>Interdit pour un transfert, recommandé sinon.</summary>
    public Guid? CategorieId { get; set; }

    public DateOnly? DateOperation { get; set; }

    [Required, MaxLength(160)]
    public string Libelle { get; set; } = "";

    [MaxLength(1000)] public string? Note { get; set; }
    [MaxLength(120)]  public string? Tiers { get; set; }
    [MaxLength(30)]   public string? ModePaiement { get; set; }
    [MaxLength(500)]  public string? PieceJointeUrl { get; set; }
    public bool Pointee { get; set; }
}

public class MajTransactionRequest : CreerTransactionRequest { }

/// <summary>Critères du fil de transactions. Tous les champs sont facultatifs.</summary>
public class FiltreTransactions
{
    public DateOnly? Debut { get; set; }
    public DateOnly? Fin { get; set; }
    /// <summary>depense | revenu | transfert</summary>
    public string? Type { get; set; }
    public Guid? CompteId { get; set; }
    public Guid? CategorieId { get; set; }
    public decimal? MontantMin { get; set; }
    public decimal? MontantMax { get; set; }
    /// <summary>Recherche plein texte sur le libellé et le tiers.</summary>
    public string? Recherche { get; set; }
    public bool? Pointee { get; set; }
    public int Page { get; set; } = 1;
    public int TaillePage { get; set; } = 50;
}

/// <summary>Envoi groupé depuis le mobile après une période hors ligne.</summary>
public class LotTransactionsRequest
{
    [Required, MinLength(1), MaxLength(200)]
    public List<CreerTransactionRequest> Transactions { get; set; } = new();
}
