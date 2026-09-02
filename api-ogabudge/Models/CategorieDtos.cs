using System.ComponentModel.DataAnnotations;

namespace OGABudget.Api.Models;

public class CategorieDto
{
    public Guid Id { get; set; }
    public Guid? ParentId { get; set; }
    public string Nom { get; set; } = "";
    /// <summary>depense | revenu</summary>
    public string Type { get; set; } = "depense";
    public string Icone { get; set; } = "tag";
    public string Couleur { get; set; } = "#085041";
    public bool Systeme { get; set; }
    public bool Archive { get; set; }
    public int Ordre { get; set; }
    /// <summary>Sous-catégories, remplies uniquement par l'appel arborescent.</summary>
    public List<CategorieDto> Enfants { get; set; } = new();
}

public class CreerCategorieRequest
{
    [Required, MaxLength(60)]
    public string Nom { get; set; } = "";

    [Required, MaxLength(10)]
    public string Type { get; set; } = "depense";

    public Guid? ParentId { get; set; }
    [MaxLength(40)] public string? Icone { get; set; }
    [MaxLength(9)]  public string? Couleur { get; set; }
    public int Ordre { get; set; }
}

public class MajCategorieRequest
{
    [Required, MaxLength(60)]
    public string Nom { get; set; } = "";

    public Guid? ParentId { get; set; }
    [MaxLength(40)] public string? Icone { get; set; }
    [MaxLength(9)]  public string? Couleur { get; set; }
    public bool Archive { get; set; }
    public int Ordre { get; set; }
}
