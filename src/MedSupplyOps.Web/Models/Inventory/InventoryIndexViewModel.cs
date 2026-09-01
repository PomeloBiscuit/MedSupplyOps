using MedSupplyOps.Infrastructure.Queries;

namespace MedSupplyOps.Web.Models.Inventory;

public sealed record InventoryIndexViewModel(
    DateOnly AsOf,
    IReadOnlyList<InventoryItemDetailsViewModel> Items);

public sealed record InventoryItemDetailsViewModel(
    InventoryItem Summary,
    IReadOnlyList<ItemAvailabilityLot> Lots);
