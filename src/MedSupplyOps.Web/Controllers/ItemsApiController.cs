using System.ComponentModel.DataAnnotations;
using MedSupplyOps.Infrastructure.Queries;
using MedSupplyOps.Web.Authorization;
using MedSupplyOps.Web.Models.Inventory;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MedSupplyOps.Web.Controllers;

[ApiController]
[Route("api/items")]
public sealed class ItemsApiController : ControllerBase
{
    private readonly InventoryQueries _inventoryQueries;

    public ItemsApiController(InventoryQueries inventoryQueries)
    {
        _inventoryQueries = inventoryQueries;
    }

    [HttpGet("{itemId:long}/availability")]
    [Authorize(Policy = AuthorizationPolicies.InventoryRead)]
    public async Task<ActionResult<ItemAvailabilityResponse>> GetAvailability(
        [Range(1, long.MaxValue)] long itemId,
        DateOnly? asOf,
        CancellationToken cancellationToken)
    {
        var effectiveAsOf = asOf ?? DateOnly.FromDateTime(DateTime.Today);
        var availability = await _inventoryQueries.GetItemAvailabilityAsync(itemId, effectiveAsOf, cancellationToken: cancellationToken);
        var earliestUsableExpiry = availability.Lots
            .Where(lot => lot.IsAvailable)
            .Select(lot => (DateOnly?)lot.ExpiryDate)
            .FirstOrDefault();
        var response = new ItemAvailabilityResponse(
            availability.ItemId,
            availability.AvailableQuantity,
            earliestUsableExpiry,
            availability.Lots.Select(lot => new ItemAvailabilityLotResponse(
                lot.StockLotId,
                lot.LotNumber,
                lot.ExpiryDate,
                lot.Quantity,
                lot.StorageLocation,
                lot.IsAvailable)).ToList());

        return Ok(response);
    }
}
