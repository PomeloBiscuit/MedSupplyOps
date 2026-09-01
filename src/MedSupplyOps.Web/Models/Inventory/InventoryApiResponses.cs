namespace MedSupplyOps.Web.Models.Inventory;

public sealed record ItemAvailabilityResponse(
    long ItemId,
    int AvailableQuantity,
    DateOnly? EarliestUsableExpiry,
    IReadOnlyList<ItemAvailabilityLotResponse> Lots);

public sealed record ItemAvailabilityLotResponse(
    long StockLotId,
    string LotNumber,
    DateOnly ExpiryDate,
    int Quantity,
    string StorageLocation,
    bool IsAvailable);

public sealed record ExpiringLotResponse(
    long StockLotId,
    long ItemId,
    string ItemCode,
    string ItemName,
    string LotNumber,
    DateOnly ExpiryDate,
    int Quantity,
    string StorageLocation);
