using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OGABudget.Api.Models;
using OGABudget.Api.Services;

namespace OGABudget.Api.Controllers;

[Route("api/auth")]
public class AuthController : ControleurAuthentifie
{
    private readonly AuthService _service;

    public AuthController(AuthService service) => _service = service;

    /// <summary>Crée un compte, ses catégories par défaut et un portefeuille « Espèces ».</summary>
    [AllowAnonymous]
    [EnableRateLimiting("inscription")]
    [HttpPost("inscription")]
    [ProducesResponseType(typeof(SessionDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErreurApi), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SessionDto>> Inscrire([FromBody] InscriptionRequest req, CancellationToken ct)
    {
        var session = await _service.InscrireAsync(req, AdresseIp, ct);
        if (session == null)
            return Conflict(new ErreurApi("Cette adresse e-mail est déjà utilisée.", "email_deja_pris"));
        return StatusCode(StatusCodes.Status201Created, session);
    }

    /// <summary>Connexion par e-mail et mot de passe.</summary>
    [AllowAnonymous]
    [EnableRateLimiting("connexion")]
    [HttpPost("connexion")]
    [ProducesResponseType(typeof(SessionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErreurApi), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<SessionDto>> Connecter([FromBody] ConnexionRequest req, CancellationToken ct)
    {
        var session = await _service.ConnecterAsync(req, AdresseIp, ct);
        if (session == null)
            return Unauthorized(new ErreurApi("E-mail ou mot de passe incorrect.", "identifiants_invalides"));
        return Ok(session);
    }

    /// <summary>Échange un refresh token contre une nouvelle paire de jetons. L'ancien est révoqué.</summary>
    [AllowAnonymous]
    [HttpPost("rafraichir")]
    [ProducesResponseType(typeof(SessionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErreurApi), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<SessionDto>> Rafraichir([FromBody] RafraichirRequest req, CancellationToken ct)
    {
        var session = await _service.RafraichirAsync(req.RefreshToken, AdresseIp, ct);
        if (session == null)
            return Unauthorized(new ErreurApi("Session expirée, reconnectez-vous.", "refresh_invalide"));
        return Ok(session);
    }

    /// <summary>Déconnecte l'appareil courant, ou tous les appareils avec <c>?tous=true</c>.</summary>
    [Authorize]
    [HttpPost("deconnexion")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Deconnecter([FromBody] RafraichirRequest? req, [FromQuery] bool tous,
                                                 CancellationToken ct)
    {
        await _service.DeconnecterAsync(UtilisateurId, req?.RefreshToken, tous, ct);
        return NoContent();
    }

    /// <summary>Profil de l'utilisateur connecté.</summary>
    [Authorize]
    [HttpGet("moi")]
    [ProducesResponseType(typeof(UtilisateurDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<UtilisateurDto>> Moi(CancellationToken ct)
    {
        var profil = await _service.ObtenirProfilAsync(UtilisateurId, ct);
        return profil == null ? NotFound() : Ok(profil);
    }

    /// <summary>Met à jour le profil. Les champs omis restent inchangés.</summary>
    [Authorize]
    [HttpPut("moi")]
    [ProducesResponseType(typeof(UtilisateurDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<UtilisateurDto>> MajProfil([FromBody] MajProfilRequest req, CancellationToken ct)
    {
        var profil = await _service.MettreAJourProfilAsync(UtilisateurId, req, ct);
        return profil == null ? NotFound() : Ok(profil);
    }

    /// <summary>Change le mot de passe et révoque toutes les sessions ouvertes.</summary>
    [Authorize]
    [HttpPost("mot-de-passe")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErreurApi), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ChangerMotDePasse([FromBody] ChangerMotDePasseRequest req, CancellationToken ct)
    {
        if (!await _service.ChangerMotDePasseAsync(UtilisateurId, req, ct))
            return BadRequest(new ErreurApi("Ancien mot de passe incorrect.", "mot_de_passe_invalide"));
        return NoContent();
    }

    /// <summary>Supprime définitivement le compte et toutes ses données.</summary>
    [Authorize]
    [HttpDelete("moi")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> SupprimerCompte(CancellationToken ct)
    {
        if (!await _service.SupprimerCompteAsync(UtilisateurId, ct)) return NotFound();
        return NoContent();
    }
}
