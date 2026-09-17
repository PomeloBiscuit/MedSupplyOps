using MedSupplyOps.Infrastructure.Queries;

namespace MedSupplyOps.Web.Models.Inventory;

public sealed record InventoryIndexViewModel(
    DateOnly AsOf,
    string? Search,
    IReadOnlyList<InventoryItemDetailsViewModel> Items);

public sealed record InventoryItemDetailsViewModel(
    InventoryItem Summary,
    IReadOnlyList<ItemAvailabilityLot> Lots,
    string SelectionLabel);
