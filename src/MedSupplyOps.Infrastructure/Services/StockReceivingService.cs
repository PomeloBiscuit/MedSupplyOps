using System.Data;
using System.Data.Common;
using System.Globalization;
using Dapper;
using MedSupplyOps.Infrastructure.Auditing;
using MedSupplyOps.Infrastructure.Identity;
using Oracle.ManagedDataAccess.Client;

namespace MedSupplyOps.Infrastructure.Services;

/// <summary>入庫服務。批次數量異動與稽核永遠在同一個交易內完成。</summary>
public sealed class StockReceivingService
{
    public const int DefaultLockWaitSeconds = 5;

    private const int OraLockWaitTimeout = 30006;
    private const int OraResourceBusy = 54;

    private readonly DbConnection _connection;
    private readonly ICurrentUser _currentUser;

    public StockReceivingService(DbConnection connection, ICurrentUser currentUser)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(currentUser);

        _connection = connection;
        _currentUser = currentUser;
    }

    public async Task<ReceiveResult> ReceiveAsync(
        long itemId,
        string lotNumber,
        DateOnly expiryDate,
        int quantity,
        string storageLocation,
        DateOnly asOf,
        int lockWaitSeconds = DefaultLockWaitSeconds,
        CancellationToken cancellationToken = default)
    {
        var normalizedLot = NormalizeKey(lotNumber);
        var normalizedLocation = NormalizeKey(storageLocation);
        ValidateInput(normalizedLot, expiryDate, quantity, normalizedLocation, asOf, lockWaitSeconds);

        if (itemId <= 0)
        {
            return ReceiveResult.ItemNotFound();
        }

        if (_connection.State != ConnectionState.Open)
        {
            await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var transaction = await _connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            // ★ 必須先鎖品項列：
            // 1. 兩人同時入庫同一個新批號時，讓第二人等到第一人提交後再重讀批次，
            //    因而走「既有批次加量」，不會兩邊都 INSERT 後由第二人撞唯一鍵。
            // 2. 停用品項也鎖同一列，避免它在「確認無庫存」與軟刪除之間被另一人入庫。
            // 3. 固定順序是「品項列 → 批次列」；發料只鎖批次列、從不等待品項列，
            //    所以兩條路徑不會形成等待環。
            var lockedItem = await _connection.QuerySingleOrDefaultAsync<LockedItemRow>(new CommandDefinition(
                BuildLockSql(LockItemSqlHead, lockWaitSeconds),
                new { itemId },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            if (lockedItem is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return ReceiveResult.ItemNotFound();
            }

            if (expiryDate < asOf)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return ReceiveResult.Expired();
            }

            var existing = await _connection.QuerySingleOrDefaultAsync<LockedStockLotRow>(new CommandDefinition(
                BuildLockSql(LockStockLotSqlHead, lockWaitSeconds),
                new { itemId, lot = normalizedLot, location = normalizedLocation },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            if (existing is not null && DateOnly.FromDateTime(existing.ExpiryDate) != expiryDate)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return ReceiveResult.ExpiryMismatch(DateOnly.FromDateTime(existing.ExpiryDate));
            }

            var actor = _currentUser.Actor;
            long stockLotId;
            int quantityBefore;

            if (existing is null)
            {
                await _connection.ExecuteAsync(new CommandDefinition(
                    InsertStockLotSql,
                    new
                    {
                        itemId,
                        lot = normalizedLot,
                        expiry = expiryDate.ToDateTime(TimeOnly.MinValue),
                        qty = quantity,
                        location = normalizedLocation,
                        actor,
                    },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

                stockLotId = decimal.ToInt64(await _connection.QuerySingleAsync<decimal>(new CommandDefinition(
                    SelectStockLotIdSql,
                    new { itemId, lot = normalizedLot, location = normalizedLocation },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false));
                quantityBefore = 0;
            }
            else
            {
                stockLotId = decimal.ToInt64(existing.StockLotId);
                quantityBefore = decimal.ToInt32(existing.Quantity);
                await _connection.ExecuteAsync(new CommandDefinition(
                    AddQuantitySql,
                    new { qty = quantity, actor, stockLotId },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            // 從資料庫重讀入庫後數量，避免把 C# 計算值誤用成寫回來源。
            // 真正的加法只存在於上面的 SQL：quantity = quantity + :qty。
            var quantityAfter = decimal.ToInt32(await _connection.QuerySingleAsync<decimal>(new CommandDefinition(
                SelectQuantitySql,
                new { stockLotId },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false));

            await _connection.ExecuteAsync(new CommandDefinition(
                InsertAuditLogSql,
                new
                {
                    entityType = AuditValues.StockLotEntity,
                    entityId = stockLotId.ToString(CultureInfo.InvariantCulture),
                    action = AuditValues.ReceiveAction,
                    actor,
                    oldValue = existing is null ? null : AuditValues.ToJson(new { quantity = quantityBefore }),
                    newValue = AuditValues.ToJson(new
                    {
                        itemId,
                        lotNumber = normalizedLot,
                        expiryDate,
                        storageLocation = normalizedLocation,
                        receivedQuantity = quantity,
                        quantityAfter,
                    }),
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ReceiveResult.Success(stockLotId, quantityAfter);
        }
        catch (OracleException exception) when (exception.Number is OraLockWaitTimeout or OraResourceBusy)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return ReceiveResult.LockTimeout();
        }
    }

    private static string NormalizeKey(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();

    private static void ValidateInput(
        string lotNumber,
        DateOnly expiryDate,
        int quantity,
        string storageLocation,
        DateOnly asOf,
        int lockWaitSeconds)
    {
        if (lotNumber.Length is < 1 or > 64)
        {
            throw new ArgumentException("批號必填且不可超過 64 個字。", nameof(lotNumber));
        }

        if (storageLocation.Length is < 1 or > 64)
        {
            throw new ArgumentException("儲藏位置必填且不可超過 64 個字。", nameof(storageLocation));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(quantity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quantity, 100_000);
        ArgumentOutOfRangeException.ThrowIfEqual(expiryDate, default);
        ArgumentOutOfRangeException.ThrowIfEqual(asOf, default);
        ArgumentOutOfRangeException.ThrowIfNegative(lockWaitSeconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lockWaitSeconds, 3600);
    }

    private static string BuildLockSql(string sqlHead, int waitSeconds)
    {
        // WAIT 與秒數之間的空白必須明確補上；直接串接會形成 WAIT5，Oracle 會回 ORA-00920。
        return sqlHead + " " + waitSeconds.ToString(CultureInfo.InvariantCulture);
    }

    private const string LockItemSqlHead = """
        SELECT item_id AS ItemId
        FROM items
        WHERE item_id = :itemId
          AND is_deleted = 0
        FOR UPDATE WAIT
        """;

    private const string LockStockLotSqlHead = """
        SELECT stock_lot_id AS StockLotId,
               expiry_date AS ExpiryDate,
               quantity AS Quantity
        FROM stock_lots
        WHERE item_id = :itemId
          AND lot_number = :lot
          AND storage_location = :location
        FOR UPDATE WAIT
        """;

    private const string InsertStockLotSql = """
        INSERT INTO stock_lots
            (item_id, lot_number, expiry_date, quantity, storage_location, created_by)
        VALUES
            (:itemId, :lot, :expiry, :qty, :location, :actor)
        """;

    private const string SelectStockLotIdSql = """
        SELECT stock_lot_id
        FROM stock_lots
        WHERE item_id = :itemId
          AND lot_number = :lot
          AND storage_location = :location
        """;

    private const string AddQuantitySql = """
        UPDATE stock_lots
        SET quantity = quantity + :qty,
            row_version = row_version + 1,
            updated_at = SYS_EXTRACT_UTC(SYSTIMESTAMP),
            updated_by = :actor
        WHERE stock_lot_id = :stockLotId
        """;

    private const string SelectQuantitySql = """
        SELECT quantity
        FROM stock_lots
        WHERE stock_lot_id = :stockLotId
        """;

    private const string InsertAuditLogSql = """
        INSERT INTO audit_logs
            (entity_type, entity_id, action, actor, old_value, new_value)
        VALUES
            (:entityType, :entityId, :action, :actor, :oldValue, :newValue)
        """;

    private sealed class LockedItemRow
    {
        public decimal ItemId { get; init; }
    }

    private sealed class LockedStockLotRow
    {
        public decimal StockLotId { get; init; }
        public DateTime ExpiryDate { get; init; }
        public decimal Quantity { get; init; }
    }
}

public enum ReceiveFailureReason
{
    None,
    ItemNotFound,
    Expired,
    ExpiryMismatch,
    LockTimeout,
}

public sealed class ReceiveResult
{
    private ReceiveResult(
        ReceiveFailureReason failureReason,
        long stockLotId = 0,
        int quantityAfter = 0,
        DateOnly? existingExpiry = null)
    {
        FailureReason = failureReason;
        StockLotId = stockLotId;
        QuantityAfter = quantityAfter;
        ExistingExpiry = existingExpiry;
    }

    public bool IsSuccess => FailureReason == ReceiveFailureReason.None;
    public ReceiveFailureReason FailureReason { get; }
    public long StockLotId { get; }
    public int QuantityAfter { get; }
    public DateOnly? ExistingExpiry { get; }

    public static ReceiveResult Success(long stockLotId, int quantityAfter)
        => new(ReceiveFailureReason.None, stockLotId, quantityAfter);

    public static ReceiveResult ItemNotFound() => new(ReceiveFailureReason.ItemNotFound);

    public static ReceiveResult Expired() => new(ReceiveFailureReason.Expired);

    public static ReceiveResult ExpiryMismatch(DateOnly existingExpiry)
        => new(ReceiveFailureReason.ExpiryMismatch, existingExpiry: existingExpiry);

    public static ReceiveResult LockTimeout() => new(ReceiveFailureReason.LockTimeout);
}
