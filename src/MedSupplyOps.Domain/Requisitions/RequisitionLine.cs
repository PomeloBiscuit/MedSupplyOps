namespace MedSupplyOps.Domain.Requisitions;

/// <summary>
/// 請領明細：一張單裡的一個品項與數量。
///
/// 若把品項直接掛在請領單上，一張單就只能有一個品項。
/// 拆出明細表是資料模型層級的決策，不是新增功能（見 docs/requirements.md MIG-1）。
/// </summary>
public sealed class RequisitionLine
{
    public RequisitionLine(long itemId, int quantity)
    {
        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "請領數量必須為正整數。");
        }

        ItemId = itemId;
        Quantity = quantity;
    }

    public long ItemId { get; }
    public int Quantity { get; }
}
