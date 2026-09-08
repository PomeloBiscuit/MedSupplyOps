using MedSupplyOps.Infrastructure.Queries;
using MedSupplyOps.Web.Authorization;
using MedSupplyOps.Web.Models.Inventory;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MedSupplyOps.Web.Controllers;

[ApiController]
[Route("api/inventory")]
public sealed class InventoryApiController : ControllerBase
{
    private readonly InventoryQueries _inventoryQueries;

    public InventoryApiController(InventoryQueries inventoryQueries)
    {
        _inventoryQueries = inventoryQueries;
    }

    [HttpGet("expiring")]
    [Authorize(Policy = AuthorizationPolicies.InventoryRead)]
    public async Task<ActionResult<IReadOnlyList<ExpiringLotResponse>>> GetExpiring(
        int withinDays = 30,
        DateOnly? asOf = null,
        CancellationToken cancellationToken = default)
    {
        if (withinDays < 0)
        {
            ModelState.AddModelError(nameof(withinDays), "天數不得為負數。");
            return ValidationProblem(ModelState);
        }

        var effectiveAsOf = asOf ?? DateOnly.FromDateTime(DateTime.Today);
        var lots = await _inventoryQueries.GetExpiringLotsAsync(withinDays, effectiveAsOf, cancellationToken: cancellationToken);
        return Ok(lots.Select(lot => new ExpiringLotResponse(
            lot.StockLotId,
            lot.ItemId,
            lot.ItemCode,
            lot.ItemName,
            lot.LotNumber,
            lot.ExpiryDate,
            lot.Quantity,
            lot.StorageLocation)).ToList());
    }
}
