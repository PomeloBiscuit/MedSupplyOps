using MedSupplyOps.Infrastructure.Queries;

namespace MedSupplyOps.Web.Models.Inventory;

public sealed record ExpiringLotsViewModel(
    int WithinDays,
    DateOnly AsOf,
    IReadOnlyList<ExpiringLot> Lots);
