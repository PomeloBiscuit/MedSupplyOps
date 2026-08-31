using System.Data;
using System.Data.Common;
using System.Globalization;
using Dapper;
using MedSupplyOps.Domain.Inventory;
using Oracle.ManagedDataAccess.Client;

namespace MedSupplyOps.Infrastructure.Services;

/// <summary>
/// 發料服務（FR-303 / FR-401 / FR-402）。
///
/// ─────────────────────────────────────────────────────────────────────────
/// 這是整個系統最容易「做錯了但畫面完全正常」的地方，所以把設計理由寫在這裡。
///
/// 【問題長什麼樣】
/// 兩個科室同時請領最後一箱。兩條連線各自讀到「可用量 5」，各自判斷「夠」，
/// 各自扣 5 —— 兩張單都成功、畫面都顯示發料完成、庫存卻變成 -5。
/// 這在單執行緒的測試裡永遠測不出來，只有上線後才會出事。
///
/// 【為什麼用悲觀鎖（SELECT ... FOR UPDATE）而不是樂觀鎖】
/// schema 裡有 row_version 欄位，樂觀鎖也做得到「不超發」。但兩者的使用者體驗不同：
///   樂觀鎖 → 第二個人做完所有事，最後才被告知「資料已被他人變更，請重試」。
///   悲觀鎖 → 第二個人在讀取階段就等待，等到之後看到的是「已經被領走後」的真實庫存，
///            於是他得到的是「庫存不足，目前可用 0」這種對他有意義的訊息。
/// 發料是短交易、高衝突、且結果對使用者是「能不能領到」——
/// 讓他等一下拿到正確答案，比讓他做完再重來合理。
///
/// 【為什麼是 WAIT n 而不是 NOWAIT，也不是無限等待】
///   NOWAIT   → 一有競爭就立刻失敗，正常的短暫重疊會變成大量假錯誤。
///   無限等待 → 一個卡住的交易會讓整條發料佇列停擺，而且沒有任何徵兆。
///   WAIT n   → 等得起就等，等不到就回一個「可重試」的明確結果。
///
/// 【★ 資料庫層仍是最後一道防線，不可省】
/// stock_lots 有 CHECK (quantity >= 0)。就算這裡的鎖有漏洞、或日後有人寫了
/// 另一條繞過本服務的路徑，超發也會被擋成 ORA-02290 而不是寫出負庫存。
/// 應用層負責「給出正確且友善的結果」，資料庫層負責「保證資料永遠不會錯」。
/// 兩者不是重複，是不同層次的責任。
///
/// 【呼叫端的義務：多品項時必須以一致的順序處理】
/// 本服務一次只處理一個品項。一張請領單有多個品項時，呼叫端必須
/// 依 item_id 遞增的順序逐一發料。若兩張單以相反順序取鎖，就會死結。
/// 這個約束沒辦法在這裡強制，所以寫在這裡並在測試中釘住。
/// ─────────────────────────────────────────────────────────────────────────
/// </summary>
public sealed class StockIssueService
{
    /// <summary>等待列鎖的秒數上限。逾時回傳 <see cref="IssueFailureReason.LockTimeout"/>。</summary>
    public const int DefaultLockWaitSeconds = 5;

    private const int OraLockWaitTimeout = 30006;   // ORA-30006: resource busy; WAIT timeout expired
    private const int OraResourceBusy = 54;         // ORA-00054: resource busy with NOWAIT

    private readonly DbConnection _connection;
    private readonly int _lockWaitSeconds;
    private readonly Func<CancellationToken, Task>? _afterLockAcquired;

    /// <param name="connection">連線。生命週期由呼叫端管理。</param>
    /// <param name="lockWaitSeconds">等待列鎖的秒數上限。</param>
    /// <param name="afterLockAcquired">
    /// **測試用的同步點**，在取得列鎖之後、扣減之前被呼叫。生產程式碼一律傳 <c>null</c>。
    ///
    /// 為什麼要在正式碼裡留這個縫：並發測試若靠 <c>Thread.Sleep</c> 去「製造」競態，
    /// 結果會取決於當下的排程 —— 那種測試會時綠時紅，而**偶爾綠比一直紅更糟**，
    /// 因為它會訓練人忽略紅燈。有了這個明確的同步點，
    /// 「兩條交易都讀完才開始寫」這件事就是**決定性**的，不是碰運氣。
    ///
    /// 這個縫本身是可被驗證的：鑑別力探針把 FOR UPDATE 拿掉時，
    /// 對應的測試必須變紅（見 scripts/mutation-probe.ps1 的 P7）。
    /// </param>
    public StockIssueService(
        DbConnection connection,
        int lockWaitSeconds = DefaultLockWaitSeconds,
        Func<CancellationToken, Task>? afterLockAcquired = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentOutOfRangeException.ThrowIfNegative(lockWaitSeconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lockWaitSeconds, 3600);

        _connection = connection;
        _lockWaitSeconds = lockWaitSeconds;
        _afterLockAcquired = afterLockAcquired;
    }

    /// <summary>
    /// 對單一品項執行 FEFO 發料，並在同一個交易內扣減批次與寫入配批紀錄。
    ///
    /// 本方法**自行開啟並結束交易**。理由：列鎖必須從 SELECT ... FOR UPDATE 一路持有到
    /// COMMIT，中間交給呼叫端就等於把並發保證交出去了。
    /// 副作用是整合測試不能靠「包在交易裡再 rollback」來清資料 —— 但那本來就不可行：
    /// **並發行為的本質就是「跨 commit 之後會發生什麼」，在單一個 rollback 交易裡測不到。**
    /// </summary>
    public async Task<IssueResult> IssueAsync(
        long requisitionLineId,
        long itemId,
        int requestedQuantity,
        DateOnly asOf,
        string issuedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuedBy);

        if (requestedQuantity <= 0)
        {
            return IssueResult.InvalidQuantity(requestedQuantity);
        }

        if (_connection.State != ConnectionState.Open)
        {
            await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var transaction = await _connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        List<LockedLotRow> lockedRows;
        try
        {
            lockedRows = (await _connection
                .QueryAsync<LockedLotRow>(new CommandDefinition(
                    BuildLockSql(_lockWaitSeconds),
                    new { itemId },
                    transaction,
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false)).AsList();
        }
        catch (OracleException ex) when (ex.Number is OraLockWaitTimeout or OraResourceBusy)
        {
            // 等不到鎖。這不是「庫存不足」—— 庫存可能綽綽有餘，只是現在被別人鎖著。
            // 回一個可重試的明確結果，不要讓使用者看到假的缺貨訊息。
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return IssueResult.LockTimeout(requestedQuantity);
        }

        if (_afterLockAcquired is not null)
        {
            // 測試用的同步點。放在「鎖已取得、尚未扣減」之間 ——
            // 這正是競態存在的那個窗口，也是唯一值得刻意打開來觀察的位置。
            await _afterLockAcquired(cancellationToken).ConfigureAwait(false);
        }

        // 用 Domain 的 FEFO 演算法對「已鎖定的快照」配批。
        // 配批規則只有一份實作（MedSupplyOps.Domain.Inventory.FefoAllocator），
        // 不在 SQL 裡再寫一次 —— 否則兩處會各自演化，而不一致的症狀是「發錯批次」，
        // 畫面完全正常。
        var domainLots = lockedRows.ConvertAll(row => new StockLot(
            decimal.ToInt64(row.StockLotId),
            itemId,
            row.LotNumber,
            DateOnly.FromDateTime(row.ExpiryDate),
            decimal.ToInt32(row.Quantity),
            row.StorageLocation));

        var allocation = FefoAllocator.Allocate(domainLots, requestedQuantity, asOf);

        if (!allocation.IsSuccess)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return allocation.FailureReason == AllocationFailureReason.InvalidQuantity
                ? IssueResult.InvalidQuantity(requestedQuantity)
                : IssueResult.InsufficientStock(requestedQuantity, allocation.AvailableQuantity);
        }

        foreach (var line in allocation.Allocations)
        {
            await _connection.ExecuteAsync(new CommandDefinition(
                DeductSql,
                new { qty = line.Quantity, actor = issuedBy, lotId = line.StockLotId },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            await _connection.ExecuteAsync(new CommandDefinition(
                InsertAllocationSql,
                new
                {
                    lineId = requisitionLineId,
                    lotId = line.StockLotId,
                    qty = line.Quantity,
                    expiry = line.ExpiryDate.ToDateTime(TimeOnly.MinValue),
                    actor = issuedBy,
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return IssueResult.Success(allocation.Allocations, requestedQuantity, allocation.AvailableQuantity);
    }

    /// <summary>
    /// 取得並鎖定某品項所有「還有數量」的批次。
    ///
    /// 為什麼只鎖 quantity > 0：數量為 0 的批次我們永遠不會扣，鎖它只會增加無謂的競爭。
    /// 為什麼不在這裡濾掉過期批次：過期與否由 Domain 的 FefoAllocator 依 asOf 判定，
    /// 判斷規則只能有一份。這裡多鎖幾列的成本，遠低於「同一條規則寫在兩個地方」的風險。
    ///
    /// ORDER BY stock_lot_id 是為了讓所有交易以相同順序碰同一批列，降低死結機率。
    /// （Oracle 的 FOR UPDATE 實際上依存取路徑上鎖，ORDER BY 不保證上鎖順序，
    ///   但同樣的述詞與計畫會走同樣的路徑；真正的跨品項順序約束在呼叫端，見類別註解。）
    /// </summary>
    private static string BuildLockSql(int waitSeconds)
    {
        // waitSeconds 在建構子已限制為 0..3600 的整數，這裡以不變文化格式化後串接。
        // 它不是使用者輸入，也不可能帶入 SQL 片段。
        //
        // 空白必須自己補：C# 原始字串常值不會保留最後一行的結尾換行，
        // 直接串接會得到 "FOR UPDATE WAIT5"。這種錯誤 Oracle 會回 ORA-00920，
        // 訊息是「無效的關聯運算子」—— 完全看不出是少了一個空格。
        return LockSqlHead + " " + waitSeconds.ToString(CultureInfo.InvariantCulture);
    }

    private const string LockSqlHead = """
        SELECT stock_lot_id AS StockLotId,
               lot_number   AS LotNumber,
               expiry_date  AS ExpiryDate,
               quantity     AS Quantity,
               storage_location AS StorageLocation
        FROM stock_lots
        WHERE item_id = :itemId
          AND quantity > 0
        ORDER BY stock_lot_id
        FOR UPDATE WAIT
        """;

    private const string DeductSql = """
        UPDATE stock_lots
        SET quantity   = quantity - :qty,
            row_version = row_version + 1,
            updated_at = SYS_EXTRACT_UTC(SYSTIMESTAMP),
            updated_by = :actor
        WHERE stock_lot_id = :lotId
        """;

    private const string InsertAllocationSql = """
        INSERT INTO issue_allocations
            (requisition_line_id, stock_lot_id, quantity, expiry_date_at_issue, issued_by)
        VALUES
            (:lineId, :lotId, :qty, :expiry, :actor)
        """;

    private sealed class LockedLotRow
    {
        public decimal StockLotId { get; init; }
        public string LotNumber { get; init; } = string.Empty;
        public DateTime ExpiryDate { get; init; }
        public decimal Quantity { get; init; }
        public string StorageLocation { get; init; } = string.Empty;
    }
}
