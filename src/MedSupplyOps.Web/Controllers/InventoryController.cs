using MedSupplyOps.Infrastructure.Queries;
using MedSupplyOps.Infrastructure.Time;
using MedSupplyOps.Web.Authorization;
using MedSupplyOps.Web.Models.Inventory;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MedSupplyOps.Web.Controllers;

public sealed class InventoryController : Controller
{
    private readonly InventoryQueries _inventoryQueries;
    private readonly BusinessCalendar _businessCalendar;

    public InventoryController(InventoryQueries inventoryQueries, BusinessCalendar businessCalendar)
    {
        _inventoryQueries = inventoryQueries;
        _businessCalendar = businessCalendar;
    }

    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.InventoryRead)]
    public async Task<IActionResult> Index(DateOnly? asOf, CancellationToken cancellationToken)
    {
        var effectiveAsOf = asOf ?? _businessCalendar.Today;
        var summaries = await _inventoryQueries.GetInventoryItemsAsync(effectiveAsOf, cancellationToken: cancellationToken);
        var itemDetails = new List<InventoryItemDetailsViewModel>(summaries.Count);
        foreach (var summary in summaries)
        {
            var availability = await _inventoryQueries.GetItemAvailabilityAsync(
                summary.ItemId,
                effectiveAsOf,
                cancellationToken: cancellationToken);
            itemDetails.Add(new InventoryItemDetailsViewModel(summary, availability.Lots));
        }

        return View(new InventoryIndexViewModel(effectiveAsOf, itemDetails));
    }

    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.InventoryRead)]
    public async Task<IActionResult> Expiring(int withinDays = 30, DateOnly? asOf = null, CancellationToken cancellationToken = default)
    {
        if (withinDays < 0)
        {
            ModelState.AddModelError(nameof(ExpiringLotsViewModel.WithinDays), "天數不得為負數。");
            return View(new ExpiringLotsViewModel(0, asOf ?? _businessCalendar.Today, []));
        }

        var effectiveAsOf = asOf ?? _businessCalendar.Today;
        var lots = await _inventoryQueries.GetExpiringLotsAsync(withinDays, effectiveAsOf, cancellationToken: cancellationToken);
        return View(new ExpiringLotsViewModel(withinDays, effectiveAsOf, lots));
    }
}
