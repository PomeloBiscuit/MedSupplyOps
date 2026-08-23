namespace MedSupplyOps.Domain.Inventory;

/// <summary>
/// 庫存批次：「某品項 + 某批號 + 某效期 + 某儲位」的一筆庫存。數量掛在這裡，不掛在品項上。
/// （對應 docs/requirements.md 的名詞定義）
/// </summary>
public sealed class StockLot
{
    public StockLot(long id, long itemId, string lotNumber, DateOnly expiryDate, int quantity, string storageLocation)
    {
        if (quantity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "批次數量不得為負數。");
        }

        Id = id;
        ItemId = itemId;
        LotNumber = lotNumber ?? throw new ArgumentNullException(nameof(lotNumber));
        ExpiryDate = expiryDate;
        Quantity = quantity;
        StorageLocation = storageLocation ?? throw new ArgumentNullException(nameof(storageLocation));
    }

    public long Id { get; }
    public long ItemId { get; }
    public string LotNumber { get; }

    /// <summary>有效期限。語意為「這一天（含）之前仍可使用」。</summary>
    public DateOnly ExpiryDate { get; }

    public int Quantity { get; private set; }
    public string StorageLocation { get; }

    /// <summary>
    /// 是否已於 <paramref name="asOf"/> 過期。
    ///
    /// ★ 邊界定義：<c>ExpiryDate &lt; asOf</c> 才算過期，亦即「效期當天仍可用」。
    ///   醫材標示的「有效期限 2026-08-31」意思是 8/31 當天可用、9/1 起不可用。
    ///   若誤寫成 &lt;=，系統會提前一天把好貨判成過期 —— 畫面完全正常、也不會報錯，
    ///   只是每一批都少用一天。這種錯誤沒有人會發現，所以它有專屬的邊界測試。
    /// </summary>
    public bool IsExpiredOn(DateOnly asOf) => ExpiryDate < asOf;

    /// <summary>
    /// 扣減批次數量。
    ///
    /// ⚠ 這道守衛「不足以」保證並發安全。兩條執行緒各自讀到 Quantity = 5、
    ///   各自扣 5，兩邊的檢查都會通過 —— 領域層看不到彼此。
    ///   真正的防線在持久化邊界（樂觀鎖 / SELECT ... FOR UPDATE），見 FR-402。
    ///   這裡的守衛只負責「單執行緒下不會寫出負庫存」這件事。
    /// </summary>
    public void Deduct(int quantity)
    {
        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "扣減數量必須為正數。");
        }

        if (quantity > Quantity)
        {
            throw new InvalidOperationException($"批次 {LotNumber} 可用 {Quantity}，不足以扣減 {quantity}。");
        }

        Quantity -= quantity;
    }
}
