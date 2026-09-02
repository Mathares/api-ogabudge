using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OGABudget.Api.Models;
using OGABudget.Api.Services;

namespace OGABudget.Api.Controllers;

[Authorize]
[Route("api/categories")]
public class CategoriesController : ControleurAuthentifie
{
    private readonly CategorieService _service;

    public CategoriesController(CategorieService service) => _service = service;

    /// <summary>Catégories de l'utilisateur.</summary>
    /// <param name="type">« depense » ou « revenu ». Omis, les deux sont renvoyées.</param>
    /// <param name="arborescence">Vrai pour imbriquer les sous-catégories dans leur parent.</param>
    [HttpGet]
    [ProducesResponseType(typeof(List<CategorieDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<CategorieDto>>> Lister([FromQuery] string? type,
                                                               [FromQuery] bool inclureArchivees,
                                                               [FromQuery] bool arborescence,
                                                               CancellationToken ct)
        => Ok(await _service.ListerAsync(UtilisateurId, type?.ToLowerInvariant(), inclureArchivees, arborescence, ct));

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(CategorieDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CategorieDto>> Obtenir(Guid id, CancellationToken ct)
    {
        var categorie = await _service.ObtenirAsync(UtilisateurId, id, ct);
        return categorie == null ? NotFound() : Ok(categorie);
    }

    [HttpPost]
    [ProducesResponseType(typeof(CategorieDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErreurApi), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CategorieDto>> Creer([FromBody] CreerCategorieRequest req, CancellationToken ct)
    {
        var id = await _service.CreerAsync(UtilisateurId, req, ct);
        if (id == null)
            return BadRequest(new ErreurApi(
                "Type invalide, parent introuvable ou de type différent, ou catégorie déjà existante.",
                "categorie_invalide"));

        var categorie = await _service.ObtenirAsync(UtilisateurId, id.Value, ct);
        return CreatedAtAction(nameof(Obtenir), new { id = id.Value }, categorie);
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(CategorieDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CategorieDto>> MettreAJour(Guid id, [FromBody] MajCategorieRequest req,
                                                               CancellationToken ct)
    {
        if (!await _service.MettreAJourAsync(UtilisateurId, id, req, ct)) return NotFound();
        return Ok(await _service.ObtenirAsync(UtilisateurId, id, ct));
    }

    /// <summary>
    /// Supprime une catégorie. Refusé si des opérations y sont rattachées, sauf <c>?forcer=true</c> :
    /// les transactions sont alors conservées et basculées en « Sans catégorie ».
    /// </summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErreurApi), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Supprimer(Guid id, [FromQuery] bool forcer, CancellationToken ct)
    {
        var (supprimee, utilisee, _) = await _service.SupprimerAsync(UtilisateurId, id, forcer, ct);
        if (!supprimee && utilisee > 0)
            return Conflict(new ErreurApi(
                $"{utilisee} opération(s) utilisent cette catégorie. Archivez-la, ou relancez avec forcer=true : les opérations passeront en « Sans catégorie ».",
                "categorie_utilisee"));
        return supprimee ? NoContent() : NotFound();
    }
}
