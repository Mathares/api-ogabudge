using System.ComponentModel.DataAnnotations;

namespace OGABudget.Api.Models;

public class ObjectifDto
{
    public Guid Id { get; set; }
    public string Nom { get; set; } = "";
    public string? Description { get; set; }
    public decimal MontantCible { get; set; }
    public decimal MontantActuel { get; set; }
    public decimal MontantRestant { get; set; }
    public decimal PourcentageAtteint { get; set; }
    public DateOnly? DateEcheance { get; set; }
    public int? JoursRestants { get; set; }
    /// <summary>Épargne à mettre de côté chaque mois pour tenir l'échéance.</summary>
    public decimal? EffortMensuelRequis { get; set; }
    public Guid? CompteId { get; set; }
    public string? CompteNom { get; set; }
    public string Couleur { get; set; } = "#ffb400";
    public string Icone { get; set; } = "target";
    /// <summary>en_cours | atteint | abandonne</summary>
    public string Statut { get; set; } = "en_cours";
    public DateTimeOffset DateCreation { get; set; }
}

public class CreerObjectifRequest
{
    [Required, MaxLength(80)]
    public string Nom { get; set; } = "";

    [MaxLength(500)] public string? Description { get; set; }

    [Range(0.01, 999999999999.99)]
    public decimal MontantCible { get; set; }

    public DateOnly? DateEcheance { get; set; }
    public Guid? CompteId { get; set; }
    [MaxLength(9)]  public string? Couleur { get; set; }
    [MaxLength(40)] public string? Icone { get; set; }
}

public class MajObjectifRequest : CreerObjectifRequest
{
    /// <summary>en_cours | atteint | abandonne</summary>
    [MaxLength(15)]
    public string Statut { get; set; } = "en_cours";
}

public class VersementDto
{
    public Guid Id { get; set; }
    public Guid ObjectifId { get; set; }
    public decimal Montant { get; set; }
    public DateOnly DateVersement { get; set; }
    public string? Note { get; set; }
    public Guid? TransactionId { get; set; }
    public DateTimeOffset DateCreation { get; set; }
}

public class CreerVersementRequest
{
    /// <summary>Négatif pour retirer de l'objectif.</summary>
    [Required]
    public decimal Montant { get; set; }

    public DateOnly? DateVersement { get; set; }
    [MaxLength(300)] public string? Note { get; set; }

    /// <summary>Génère aussi un transfert vers le compte d'épargne rattaché à l'objectif.</summary>
    public bool GenererTransaction { get; set; }

    /// <summary>Compte débité quand <see cref="GenererTransaction"/> est vrai.</summary>
    public Guid? CompteSourceId { get; set; }
}
