using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OGABudget.Api.Models;
using OGABudget.Api.Services;

namespace OGABudget.Api.Controllers;

[Authorize]
[Route("api/objectifs")]
public class ObjectifsController : ControleurAuthentifie
{
    private readonly ObjectifService _service;

    public ObjectifsController(ObjectifService service) => _service = service;

    /// <param name="statut">en_cours | atteint | abandonne. Omis, tous les objectifs sont renvoyés.</param>
    [HttpGet]
    [ProducesResponseType(typeof(List<ObjectifDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<ObjectifDto>>> Lister([FromQuery] string? statut, CancellationToken ct)
        => Ok(await _service.ListerAsync(UtilisateurId, statut?.ToLowerInvariant(), ct));

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ObjectifDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ObjectifDto>> Obtenir(Guid id, CancellationToken ct)
    {
        var objectif = await _service.ObtenirAsync(UtilisateurId, id, ct);
        return objectif == null ? NotFound() : Ok(objectif);
    }

    [HttpPost]
    [ProducesResponseType(typeof(ObjectifDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErreurApi), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ObjectifDto>> Creer([FromBody] CreerObjectifRequest req, CancellationToken ct)
    {
        var id = await _service.CreerAsync(UtilisateurId, req, ct);
        if (id == null)
            return BadRequest(new ErreurApi("Montant cible invalide ou compte introuvable.", "objectif_invalide"));

        var objectif = await _service.ObtenirAsync(UtilisateurId, id.Value, ct);
        return CreatedAtAction(nameof(Obtenir), new { id = id.Value }, objectif);
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(ObjectifDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ObjectifDto>> MettreAJour(Guid id, [FromBody] MajObjectifRequest req,
                                                              CancellationToken ct)
    {
        if (!await _service.MettreAJourAsync(UtilisateurId, id, req, ct)) return NotFound();
        return Ok(await _service.ObtenirAsync(UtilisateurId, id, ct));
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Supprimer(Guid id, CancellationToken ct)
        => await _service.SupprimerAsync(UtilisateurId, id, ct) ? NoContent() : NotFound();

    // ─── Versements ─────────────────────────────────────────────────────────

    [HttpGet("{id:guid}/versements")]
    [ProducesResponseType(typeof(List<VersementDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<VersementDto>>> Versements(Guid id, CancellationToken ct)
        => Ok(await _service.ListerVersementsAsync(UtilisateurId, id, ct));

    /// <summary>
    /// Ajoute un versement (montant négatif pour un retrait). Avec
    /// <c>genererTransaction = true</c>, un transfert réel est créé du compte source
    /// vers le compte d'épargne de l'objectif.
    /// </summary>
    [HttpPost("{id:guid}/versements")]
    [ProducesResponseType(typeof(VersementDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErreurApi), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<VersementDto>> AjouterVersement(Guid id, [FromBody] CreerVersementRequest req,
                                                                    CancellationToken ct)
    {
        var (versement, erreur, introuvable) = await _service.AjouterVersementAsync(UtilisateurId, id, req, ct);
        if (introuvable) return NotFound();
        if (erreur != null) return BadRequest(new ErreurApi(erreur, "versement_invalide"));
        return StatusCode(StatusCodes.Status201Created, versement);
    }

    [HttpDelete("{id:guid}/versements/{versementId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SupprimerVersement(Guid id, Guid versementId, CancellationToken ct)
        => await _service.SupprimerVersementAsync(UtilisateurId, id, versementId, ct) ? NoContent() : NotFound();
}
