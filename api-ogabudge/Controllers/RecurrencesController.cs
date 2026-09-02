using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OGABudget.Api.Models;
using OGABudget.Api.Services;

namespace OGABudget.Api.Controllers;

[Authorize]
[Route("api/recurrences")]
public class RecurrencesController : ControleurAuthentifie
{
    private readonly RecurrenceService _service;

    public RecurrencesController(RecurrenceService service) => _service = service;

    [HttpGet]
    [ProducesResponseType(typeof(List<RecurrenceDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<RecurrenceDto>>> Lister([FromQuery] bool inclureInactives,
                                                                 CancellationToken ct)
        => Ok(await _service.ListerAsync(UtilisateurId, inclureInactives, ct));

    /// <summary>Échéances des <paramref name="jours"/> prochains jours (7 par défaut).</summary>
    [HttpGet("prochaines")]
    [ProducesResponseType(typeof(List<RecurrenceDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<RecurrenceDto>>> Prochaines([FromQuery] int jours, CancellationToken ct)
        => Ok(await _service.ProchainesAsync(UtilisateurId, jours <= 0 ? 7 : Math.Min(jours, 90), ct));

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(RecurrenceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RecurrenceDto>> Obtenir(Guid id, CancellationToken ct)
    {
        var recurrence = await _service.ObtenirAsync(UtilisateurId, id, ct);
        return recurrence == null ? NotFound() : Ok(recurrence);
    }

    [HttpPost]
    [ProducesResponseType(typeof(RecurrenceDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErreurApi), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RecurrenceDto>> Creer([FromBody] CreerRecurrenceRequest req, CancellationToken ct)
    {
        var (id, erreur) = await _service.CreerAsync(UtilisateurId, req, ct);
        if (id == null) return BadRequest(new ErreurApi(erreur ?? "Récurrence invalide.", "recurrence_invalide"));

        var recurrence = await _service.ObtenirAsync(UtilisateurId, id.Value, ct);
        return CreatedAtAction(nameof(Obtenir), new { id = id.Value }, recurrence);
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(RecurrenceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErreurApi), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RecurrenceDto>> MettreAJour(Guid id, [FromBody] MajRecurrenceRequest req,
                                                                CancellationToken ct)
    {
        var (ok, erreur) = await _service.MettreAJourAsync(UtilisateurId, id, req, ct);
        if (erreur != null) return BadRequest(new ErreurApi(erreur, "recurrence_invalide"));
        if (!ok) return NotFound();
        return Ok(await _service.ObtenirAsync(UtilisateurId, id, ct));
    }

    /// <summary>
    /// Matérialise les échéances échues en transactions réelles. Appelé par le mobile à
    /// l'ouverture ; l'opération est idempotente, la relancer ne crée pas de doublon.
    /// </summary>
    [HttpPost("generer")]
    [ProducesResponseType(typeof(GenerationRecurrencesDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<GenerationRecurrencesDto>> Generer(CancellationToken ct)
        => Ok(await _service.GenererAsync(UtilisateurId, ct));

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Supprimer(Guid id, CancellationToken ct)
        => await _service.SupprimerAsync(UtilisateurId, id, ct) ? NoContent() : NotFound();
}
