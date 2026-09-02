using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace OGABudget.Api.Controllers;

/// <summary>Sonde de disponibilité, appelée par l'hébergeur et par le mobile avant la synchronisation.</summary>
[AllowAnonymous]
[ApiController]
[Route("api/sante")]
[Produces("application/json")]
public class SanteController : ControllerBase
{
    private readonly NpgsqlDataSource _db;
    private readonly ILogger<SanteController> _logger;

    public SanteController(NpgsqlDataSource db, ILogger<SanteController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Verifier(CancellationToken ct)
    {
        var version = typeof(SanteController).Assembly.GetName().Version?.ToString() ?? "1.0.0";

        try
        {
            await using var cmd = _db.CreateCommand("SELECT 1");
            await cmd.ExecuteScalarAsync(ct);
            return Ok(new { statut = "ok", baseDeDonnees = "ok", version, horodatage = DateTimeOffset.UtcNow });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sonde de santé : base de données injoignable.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { statut = "degrade", baseDeDonnees = "injoignable", version, horodatage = DateTimeOffset.UtcNow });
        }
    }
}
