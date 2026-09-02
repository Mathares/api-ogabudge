using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OGABudget.Api.Models;
using OGABudget.Api.Services;

namespace OGABudget.Api.Controllers;

[Authorize]
[Route("api/statistiques")]
public class StatistiquesController : ControleurAuthentifie
{
    private readonly StatistiqueService _service;

    public StatistiquesController(StatistiqueService service) => _service = service;

    /// <summary>Écran d'accueil complet : soldes, mois en cours, top dépenses, alertes, objectifs, échéances.</summary>
    [HttpGet("tableau-de-bord")]
    [ProducesResponseType(typeof(TableauDeBordDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<TableauDeBordDto>> TableauDeBord([FromQuery] DateOnly? reference,
                                                                    CancellationToken ct)
        => Ok(await _service.TableauDeBordAsync(UtilisateurId, reference, ct));

    /// <summary>Totaux d'une période. Sans dates, le mois calendaire en cours.</summary>
    [HttpGet("resume")]
    [ProducesResponseType(typeof(ResumePeriodeDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ResumePeriodeDto>> Resume([FromQuery] DateOnly? debut, [FromQuery] DateOnly? fin,
                                                             CancellationToken ct)
    {
        var (d, f) = Plage(debut, fin);
        return Ok(await _service.ResumeAsync(UtilisateurId, d, f, ct));
    }

    /// <summary>Répartition par catégorie racine, triée du plus lourd au plus léger.</summary>
    /// <param name="type">« depense » (défaut) ou « revenu ».</param>
    [HttpGet("par-categorie")]
    [ProducesResponseType(typeof(List<LigneCategorieDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<LigneCategorieDto>>> ParCategorie([FromQuery] DateOnly? debut,
                                                                          [FromQuery] DateOnly? fin,
                                                                          [FromQuery] string? type,
                                                                          CancellationToken ct)
    {
        var (d, f) = Plage(debut, fin);
        return Ok(await _service.ParCategorieAsync(UtilisateurId, d, f, type?.ToLowerInvariant() ?? "depense", ct));
    }

    /// <summary>Courbe revenus / dépenses. Les périodes sans mouvement sont renvoyées à zéro.</summary>
    /// <param name="granularite">jour | semaine | mois (défaut) | annee</param>
    [HttpGet("evolution")]
    [ProducesResponseType(typeof(List<PointEvolutionDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<PointEvolutionDto>>> Evolution([FromQuery] DateOnly? debut,
                                                                       [FromQuery] DateOnly? fin,
                                                                       [FromQuery] string? granularite,
                                                                       CancellationToken ct)
    {
        var aujourdhui = DateOnly.FromDateTime(DateTime.UtcNow);
        var d = debut ?? new DateOnly(aujourdhui.Year, aujourdhui.Month, 1).AddMonths(-11);
        var f = fin ?? aujourdhui;
        var g = granularite?.ToLowerInvariant() switch
        {
            "jour" or "semaine" or "mois" or "annee" => granularite!.ToLowerInvariant(),
            _ => "mois"
        };
        return Ok(await _service.EvolutionAsync(UtilisateurId, d, f, g, ct));
    }

    /// <summary>Par défaut, le mois calendaire en cours.</summary>
    private static (DateOnly debut, DateOnly fin) Plage(DateOnly? debut, DateOnly? fin)
    {
        var aujourdhui = DateOnly.FromDateTime(DateTime.UtcNow);
        var d = debut ?? new DateOnly(aujourdhui.Year, aujourdhui.Month, 1);
        var f = fin ?? d.AddMonths(1).AddDays(-1);
        return f < d ? (f, d) : (d, f);
    }
}
