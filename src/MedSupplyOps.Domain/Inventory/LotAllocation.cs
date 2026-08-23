namespace MedSupplyOps.Domain.Inventory;

/// <summary>單一批次的配批結果：從哪一批、取多少。</summary>
public sealed record LotAllocation(long StockLotId, string LotNumber, DateOnly ExpiryDate, int Quantity);
