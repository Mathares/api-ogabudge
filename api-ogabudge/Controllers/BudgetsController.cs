using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OGABudget.Api.Models;
using OGABudget.Api.Services;

namespace OGABudget.Api.Controllers;

[Authorize]
[Route("api/budgets")]
public class BudgetsController : ControleurAuthentifie
{
    private readonly BudgetService _service;

    public BudgetsController(BudgetService service) => _service = service;

    /// <summary>
    /// Budgets avec leur consommation sur la période en cours.
    /// <paramref name="reference"/> permet de consulter une période passée.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<BudgetDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<BudgetDto>>> Lister([FromQuery] bool inclureInactifs,
                                                            [FromQuery] DateOnly? reference,
                                                            CancellationToken ct)
        => Ok(await _service.ListerAsync(UtilisateurId, inclureInactifs, reference, ct));

    /// <summary>Budgets ayant franchi leur seuil d'alerte : la matière à notification.</summary>
    [HttpGet("alertes")]
    [ProducesResponseType(typeof(List<BudgetDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<BudgetDto>>> Alertes(CancellationToken ct)
        => Ok(await _service.EnAlerteAsync(UtilisateurId, ct));

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(BudgetDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BudgetDto>> Obtenir(Guid id, [FromQuery] DateOnly? reference, CancellationToken ct)
    {
        var budget = await _service.ObtenirAsync(UtilisateurId, id, reference, ct);
        return budget == null ? NotFound() : Ok(budget);
    }

    [HttpPost]
    [ProducesResponseType(typeof(BudgetDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErreurApi), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<BudgetDto>> Creer([FromBody] CreerBudgetRequest req, CancellationToken ct)
    {
        var (id, erreur) = await _service.CreerAsync(UtilisateurId, req, ct);
        if (id == null) return BadRequest(new ErreurApi(erreur ?? "Budget invalide.", "budget_invalide"));

        var budget = await _service.ObtenirAsync(UtilisateurId, id.Value, null, ct);
        return CreatedAtAction(nameof(Obtenir), new { id = id.Value }, budget);
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(BudgetDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErreurApi), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BudgetDto>> MettreAJour(Guid id, [FromBody] MajBudgetRequest req,
                                                            CancellationToken ct)
    {
        var (ok, erreur) = await _service.MettreAJourAsync(UtilisateurId, id, req, ct);
        if (erreur != null) return BadRequest(new ErreurApi(erreur, "budget_invalide"));
        if (!ok) return NotFound();
        return Ok(await _service.ObtenirAsync(UtilisateurId, id, null, ct));
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Supprimer(Guid id, CancellationToken ct)
        => await _service.SupprimerAsync(UtilisateurId, id, ct) ? NoContent() : NotFound();
}
