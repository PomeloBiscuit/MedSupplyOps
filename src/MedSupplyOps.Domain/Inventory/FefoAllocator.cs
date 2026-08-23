namespace MedSupplyOps.Domain.Inventory;

/// <summary>
/// FEFO（First Expired, First Out，先到期先出）配批演算法。對應需求 FR-401。
///
/// 這是本專案最容易「做錯了但畫面完全正常」的地方：配錯批次時，數量對、庫存扣得剛好、
/// 頁面不會有任何異狀 —— 唯一的差別是發出去的醫材是不是快過期的那一批。
/// 因此它被獨立成純函式，並且有一組專門釘住邊界的測試。
/// </summary>
public static class FefoAllocator
{
    /// <summary>
    /// 依 FEFO 從多個批次配出指定數量。
    /// </summary>
    /// <param name="lots">候選批次（同一品項）。順序不拘，本方法自行排序。</param>
    /// <param name="requestedQuantity">請求數量，必須為正整數。</param>
    /// <param name="asOf">
    /// 判定過期的基準日。
    /// ★ 刻意由呼叫端傳入，方法內部絕不呼叫 DateTime.Now —— 否則測試無法決定性，
    ///   「今天剛好沒事、明天就錯」這種缺陷會躲過所有測試。
    /// </param>
    public static AllocationResult Allocate(IEnumerable<StockLot> lots, int requestedQuantity, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(lots);

        if (requestedQuantity <= 0)
        {
            return AllocationResult.InvalidQuantity(requestedQuantity);
        }

        var usable = lots
            .Where(lot => lot.Quantity > 0)
            .Where(lot => !lot.IsExpiredOn(asOf))
            .OrderBy(lot => lot.ExpiryDate)
            // 效期相同時以「批號」決勝，不是以 Id。
            //
            // 誠實說明：單靠下面的 .ThenBy(Id) 就足以保證「決定性」，所以這一行不是為了
            // 決定性而存在，而是為了「跨環境的一致性」——
            // Id 是資料庫代理鍵，同一批資料匯入開發庫與正式庫可能拿到不同的 Id，
            // 配批結果就會跟著不同。批號則是倉管人員實際看得到、且跨環境穩定的識別。
            // 兩個環境配出不同批次這件事，畫面上完全看不出來。
            .ThenBy(lot => lot.LotNumber, StringComparer.Ordinal)
            // 最後以 Id 收尾，處理批號重複的極端情況，確保全序。
            .ThenBy(lot => lot.Id)
            .ToList();

        // 以 long 累加避免多批相加時的 int 溢位。
        var available = usable.Aggregate(0L, (sum, lot) => sum + lot.Quantity);
        var availableCapped = available > int.MaxValue ? int.MaxValue : (int)available;

        if (available < requestedQuantity)
        {
            // 不做部分發料：不足即整筆失敗（FR-401）。
            return AllocationResult.InsufficientStock(requestedQuantity, availableCapped);
        }

        var allocations = new List<LotAllocation>();
        var remaining = requestedQuantity;

        foreach (var lot in usable)
        {
            if (remaining == 0)
            {
                break;
            }

            var take = Math.Min(lot.Quantity, remaining);
            allocations.Add(new LotAllocation(lot.Id, lot.LotNumber, lot.ExpiryDate, take));
            remaining -= take;
        }

        return AllocationResult.Success(allocations, requestedQuantity, availableCapped);
    }
}
