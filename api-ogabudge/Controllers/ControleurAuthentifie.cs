using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace OGABudget.Api.Controllers;

/// <summary>
/// Base des contrôleurs authentifiés : toutes les requêtes sont cloisonnées sur
/// l'utilisateur du jeton, jamais sur un identifiant venu du corps ou de l'URL.
/// </summary>
[ApiController]
[Produces("application/json")]
public abstract class ControleurAuthentifie : ControllerBase
{
    /// <summary>Identifiant de l'utilisateur porteur du jeton.</summary>
    protected Guid UtilisateurId
    {
        get
        {
            var valeur = User.FindFirstValue(JwtRegisteredClaimNames.Sub)
                      ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(valeur, out var id) ? id : Guid.Empty;
        }
    }

    protected string? AdresseIp => HttpContext.Connection.RemoteIpAddress?.ToString();
}
