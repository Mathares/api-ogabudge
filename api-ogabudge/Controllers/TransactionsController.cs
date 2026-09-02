using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OGABudget.Api.Models;
using OGABudget.Api.Services;

namespace OGABudget.Api.Controllers;

[Authorize]
[Route("api/transactions")]
public class TransactionsController : ControleurAuthentifie
{
    private readonly TransactionService _service;

    public TransactionsController(TransactionService service) => _service = service;

    /// <summary>
    /// Fil des opérations, filtrable et paginé (50 par page par défaut, 200 au maximum).
    /// Un transfert apparaît dans le relevé de ses deux comptes.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(PageResultat<TransactionDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PageResultat<TransactionDto>>> Lister([FromQuery] FiltreTransactions filtre,
                                                                        CancellationToken ct)
        => Ok(await _service.ListerAsync(UtilisateurId, filtre, ct));

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(TransactionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TransactionDto>> Obtenir(Guid id, CancellationToken ct)
    {
        var transaction = await _service.ObtenirAsync(UtilisateurId, id, ct);
        return transaction == null ? NotFound() : Ok(transaction);
    }

    /// <summary>Enregistre une dépense, un revenu ou un transfert entre deux comptes.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(TransactionDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErreurApi), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TransactionDto>> Creer([FromBody] CreerTransactionRequest req,
                                                          CancellationToken ct)
    {
        var (transaction, erreur) = await _service.CreerAsync(UtilisateurId, req, ct);
        if (erreur != null) return BadRequest(new ErreurApi(erreur, "transaction_invalide"));
        return CreatedAtAction(nameof(Obtenir), new { id = transaction!.Id }, transaction);
    }

    /// <summary>
    /// Envoi groupé après une saisie hors ligne. Tout ou rien : une ligne invalide
    /// annule l'ensemble et la réponse indique son rang.
    /// </summary>
    [HttpPost("lot")]
    [ProducesResponseType(typeof(List<TransactionDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErreurApi), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<List<TransactionDto>>> CreerLot([FromBody] LotTransactionsRequest req,
                                                                   CancellationToken ct)
    {
        var (creees, erreur) = await _service.CreerLotAsync(UtilisateurId, req.Transactions, ct);
        if (erreur != null) return BadRequest(new ErreurApi(erreur, "lot_invalide"));
        return StatusCode(StatusCodes.Status201Created, creees);
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(TransactionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErreurApi), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TransactionDto>> MettreAJour(Guid id, [FromBody] MajTransactionRequest req,
                                                                 CancellationToken ct)
    {
        var (transaction, erreur, introuvable) = await _service.MettreAJourAsync(UtilisateurId, id, req, ct);
        if (erreur != null) return BadRequest(new ErreurApi(erreur, "transaction_invalide"));
        if (introuvable) return NotFound();
        return Ok(transaction);
    }

    /// <summary>Marque ou démarque une opération comme rapprochée du relevé.</summary>
    [HttpPatch("{id:guid}/pointage")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Pointer(Guid id, [FromQuery] bool pointee, CancellationToken ct)
        => await _service.PointerAsync(UtilisateurId, id, pointee, ct) ? NoContent() : NotFound();

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Supprimer(Guid id, CancellationToken ct)
        => await _service.SupprimerAsync(UtilisateurId, id, ct) ? NoContent() : NotFound();
}
