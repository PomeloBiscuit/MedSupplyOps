namespace MedSupplyOps.Domain.Inventory;

public enum AllocationFailureReason
{
    /// <summary>沒有失敗。</summary>
    None = 0,

    /// <summary>請求數量非正整數。</summary>
    InvalidQuantity = 1,

    /// <summary>未過期批次的可用總量不足以滿足請求。</summary>
    InsufficientStock = 2,
}
