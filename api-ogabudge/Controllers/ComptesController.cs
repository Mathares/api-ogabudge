using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OGABudget.Api.Models;
using OGABudget.Api.Services;

namespace OGABudget.Api.Controllers;

[Authorize]
[Route("api/comptes")]
public class ComptesController : ControleurAuthentifie
{
    private readonly CompteService _service;

    public ComptesController(CompteService service) => _service = service;

    /// <summary>Comptes de l'utilisateur avec leur solde courant.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<CompteDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<CompteDto>>> Lister([FromQuery] bool inclureArchives, CancellationToken ct)
        => Ok(await _service.ListerAsync(UtilisateurId, inclureArchives, ct));

    /// <summary>Somme des comptes marqués « inclus dans le total ».</summary>
    [HttpGet("solde-total")]
    [ProducesResponseType(typeof(decimal), StatusCodes.Status200OK)]
    public async Task<ActionResult<decimal>> SoldeTotal(CancellationToken ct)
        => Ok(await _service.SoldeTotalAsync(UtilisateurId, ct));

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(CompteDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CompteDto>> Obtenir(Guid id, CancellationToken ct)
    {
        var compte = await _service.ObtenirAsync(UtilisateurId, id, ct);
        return compte == null ? NotFound() : Ok(compte);
    }

    [HttpPost]
    [ProducesResponseType(typeof(CompteDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErreurApi), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CompteDto>> Creer([FromBody] CreerCompteRequest req, CancellationToken ct)
    {
        var id = await _service.CreerAsync(UtilisateurId, req, ct);
        if (id == null)
            return Conflict(new ErreurApi("Un compte porte déjà ce nom.", "nom_deja_pris"));

        var compte = await _service.ObtenirAsync(UtilisateurId, id.Value, ct);
        return CreatedAtAction(nameof(Obtenir), new { id = id.Value }, compte);
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(CompteDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CompteDto>> MettreAJour(Guid id, [FromBody] MajCompteRequest req,
                                                            CancellationToken ct)
    {
        if (!await _service.MettreAJourAsync(UtilisateurId, id, req, ct)) return NotFound();
        return Ok(await _service.ObtenirAsync(UtilisateurId, id, ct));
    }

    /// <summary>
    /// Supprime un compte. Refusé tant qu'il porte des opérations, sauf <c>?forcer=true</c>
    /// qui détruit aussi l'historique associé. Archiver le compte est presque toujours préférable.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErreurApi), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Supprimer(Guid id, [FromQuery] bool forcer, CancellationToken ct)
    {
        var (supprime, operations) = await _service.SupprimerAsync(UtilisateurId, id, forcer, ct);
        if (!supprime && operations > 0)
            return Conflict(new ErreurApi(
                $"Ce compte porte {operations} opération(s). Archivez-le, ou relancez avec forcer=true pour tout supprimer.",
                "compte_utilise"));
        return supprime ? NoContent() : NotFound();
    }
}
