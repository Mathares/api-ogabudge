using System.ComponentModel.DataAnnotations;

namespace OGABudget.Api.Models;

public class InscriptionRequest
{
    [Required, EmailAddress, MaxLength(255)]
    public string Email { get; set; } = "";

    [Required, MinLength(8), MaxLength(128)]
    public string MotDePasse { get; set; } = "";

    [Required, MaxLength(120)]
    public string NomComplet { get; set; } = "";

    [MaxLength(30)]
    public string? Telephone { get; set; }

    /// <summary>Code ISO 4217. XOF par défaut (Franc CFA).</summary>
    [MaxLength(3)]
    public string? Devise { get; set; }

    /// <summary>Nom de l'appareil, mémorisé sur la session de rafraîchissement.</summary>
    [MaxLength(120)]
    public string? Appareil { get; set; }
}

public class ConnexionRequest
{
    [Required, EmailAddress]
    public string Email { get; set; } = "";

    [Required]
    public string MotDePasse { get; set; } = "";

    [MaxLength(120)]
    public string? Appareil { get; set; }
}

public class RafraichirRequest
{
    [Required]
    public string RefreshToken { get; set; } = "";
}

public class ChangerMotDePasseRequest
{
    [Required]
    public string AncienMotDePasse { get; set; } = "";

    [Required, MinLength(8), MaxLength(128)]
    public string NouveauMotDePasse { get; set; } = "";
}

public class SessionDto
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTimeOffset ExpireLe { get; set; }
    public UtilisateurDto Utilisateur { get; set; } = new();
}

public class UtilisateurDto
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    public string NomComplet { get; set; } = "";
    public string? Telephone { get; set; }
    public string Devise { get; set; } = "XOF";
    public string Locale { get; set; } = "fr-BF";
    public string FuseauHoraire { get; set; } = "Africa/Ouagadougou";
    public string? AvatarUrl { get; set; }
    public int JourDebutMois { get; set; } = 1;
    public bool EmailVerifie { get; set; }
    public DateTimeOffset DateCreation { get; set; }
}

public class MajProfilRequest
{
    [MaxLength(120)] public string? NomComplet { get; set; }
    [MaxLength(30)]  public string? Telephone { get; set; }
    [MaxLength(3)]   public string? Devise { get; set; }
    [MaxLength(10)]  public string? Locale { get; set; }
    [MaxLength(60)]  public string? FuseauHoraire { get; set; }
    [MaxLength(500)] public string? AvatarUrl { get; set; }
    [Range(1, 28)]   public int? JourDebutMois { get; set; }
}
