using System.Data;
using Dapper;
using MedSupplyOps.Infrastructure.Identity;
using MedSupplyOps.Infrastructure.Services;
using Oracle.ManagedDataAccess.Client;
using Xunit.Abstractions;

namespace MedSupplyOps.Integration.Tests.Services;

public sealed class StockReceivingServiceTests
{
    private readonly ITestOutputHelper _output;

    public StockReceivingServiceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task T2_same_lot_with_different_expiry_is_rejected_then_matching_expiry_adds_atomically()
    {
        var itemId = await CreateItemAsync("T2");
        long stockLotId = 0;
        try
        {
            await using var setup = new OracleConnection(OracleTestDatabase.ConnectionString);
            await setup.ExecuteAsync("""
                INSERT INTO stock_lots
                    (item_id, lot_number, expiry_date, quantity, storage_location, row_version, created_by)
                VALUES
                    (:itemId, 'LOT-T2', DATE '2031-01-10', 10, 'ROOM-T2', 4, 'itest-t2')
                """, new { itemId });
            stockLotId = await setup.QuerySingleAsync<long>(
                "SELECT stock_lot_id FROM stock_lots WHERE item_id = :itemId",
                new { itemId });

            await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
            var service = new StockReceivingService(connection, new TestCurrentUser("itest-keeper@example.local"));
            var mismatch = await service.ReceiveAsync(
                itemId,
                " lot-t2 ",
                new DateOnly(2031, 1, 11),
                5,
                " room-t2 ",
                new DateOnly(2030, 12, 1));

            Assert.Equal(ReceiveFailureReason.ExpiryMismatch, mismatch.FailureReason);
            Assert.Equal(new DateOnly(2031, 1, 10), mismatch.ExistingExpiry);
            var afterMismatch = await ReadLotAsync(stockLotId);
            var auditAfterMismatch = await CountReceiveAuditsAsync(stockLotId);
            Assert.Equal(new DateTime(2031, 1, 10), afterMismatch.ExpiryDate);
            Assert.Equal(10, decimal.ToInt32(afterMismatch.Quantity));
            Assert.Equal(4, decimal.ToInt32(afterMismatch.RowVersion));
            Assert.Equal(0, auditAfterMismatch);

            var matched = await service.ReceiveAsync(
                itemId,
                " lot-t2 ",
                new DateOnly(2031, 1, 10),
                5,
                " room-t2 ",
                new DateOnly(2030, 12, 1));

            Assert.True(matched.IsSuccess);
            Assert.Equal(stockLotId, matched.StockLotId);
            Assert.Equal(15, matched.QuantityAfter);
            var afterMatched = await ReadLotAsync(stockLotId);
            var auditAfterMatched = await CountReceiveAuditsAsync(stockLotId);
            Assert.Equal(new DateTime(2031, 1, 10), afterMatched.ExpiryDate);
            Assert.Equal(15, decimal.ToInt32(afterMatched.Quantity));
            Assert.Equal(5, decimal.ToInt32(afterMatched.RowVersion));
            Assert.Equal(1, auditAfterMatched);

            await using var verify = new OracleConnection(OracleTestDatabase.ConnectionString);
            var rowCount = await verify.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM stock_lots WHERE item_id = :itemId AND lot_number = 'LOT-T2' AND storage_location = 'ROOM-T2'",
                new { itemId });
            Assert.Equal(1, rowCount);

            _output.WriteLine(
                $"T2 mismatch: result={mismatch.FailureReason}, rows=1, expiry={afterMismatch.ExpiryDate:yyyy-MM-dd}, qty={afterMismatch.Quantity}, row_version={afterMismatch.RowVersion}, audits={auditAfterMismatch}");
            _output.WriteLine(
                $"T2 match: result=Success, rows={rowCount}, expiry={afterMatched.ExpiryDate:yyyy-MM-dd}, qty={afterMatched.Quantity}, row_version={afterMatched.RowVersion}, audits={auditAfterMatched}");
        }
        finally
        {
            await CleanupItemAsync(itemId);
        }
    }

    [Fact]
    public async Task T3_two_concurrent_receipts_of_a_new_lot_wait_for_item_lock_then_merge_into_one_row()
    {
        var itemId = await CreateItemAsync("T3");
        await using var blocker = new OracleConnection(OracleTestDatabase.ConnectionString);
        await blocker.OpenAsync();
        await using var blockingTransaction = await blocker.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var blockerReleased = false;
        Task<ReceiveResult>? firstTask = null;
        Task<ReceiveResult>? secondTask = null;

        try
        {
            // QuerySingleAsync 完成代表 Oracle 已授與 FOR UPDATE 鎖；在這個明確斷言後才開始入庫，
            // 不以 sleep 或 retry 猜測鎖是否已取得。
            var lockedItemId = await blocker.QuerySingleAsync<decimal>(
                "SELECT item_id FROM items WHERE item_id = :itemId FOR UPDATE",
                new { itemId },
                blockingTransaction);
            Assert.Equal(itemId, decimal.ToInt64(lockedItemId));

            await using var firstConnection = new OracleConnection(OracleTestDatabase.ConnectionString);
            await using var secondConnection = new OracleConnection(OracleTestDatabase.ConnectionString);
            var firstService = new StockReceivingService(firstConnection, new TestCurrentUser("itest-t3-a"));
            var secondService = new StockReceivingService(secondConnection, new TestCurrentUser("itest-t3-b"));

            firstTask = firstService.ReceiveAsync(
                itemId, "lot-t3", new DateOnly(2031, 2, 1), 7, " room-t3 ", new DateOnly(2030, 1, 1));
            secondTask = secondService.ReceiveAsync(
                itemId, " LOT-T3 ", new DateOnly(2031, 2, 1), 11, "ROOM-T3", new DateOnly(2030, 1, 1));

            await Task.Delay(TimeSpan.FromSeconds(1.5));
            Assert.False(firstTask.IsCompleted, "第一筆入庫必須仍在等待測試持有的品項列鎖。");
            Assert.False(secondTask.IsCompleted, "第二筆入庫必須仍在等待測試持有的品項列鎖。");

            await blockingTransaction.RollbackAsync();
            blockerReleased = true;
            var results = await Task.WhenAll(firstTask, secondTask);
            Assert.All(results, result => Assert.True(result.IsSuccess));

            await using var verify = new OracleConnection(OracleTestDatabase.ConnectionString);
            var rows = (await verify.QueryAsync<ConcurrentLotRow>("""
                SELECT stock_lot_id AS StockLotId,
                       quantity AS Quantity
                FROM stock_lots
                WHERE item_id = :itemId
                  AND lot_number = 'LOT-T3'
                  AND storage_location = 'ROOM-T3'
                """, new { itemId })).AsList();
            Assert.Single(rows);
            Assert.Equal(18, decimal.ToInt32(rows[0].Quantity));
            var stockLotId = decimal.ToInt64(rows[0].StockLotId);
            var audits = await CountReceiveAuditsAsync(stockLotId);
            Assert.Equal(2, audits);
            _output.WriteLine(
                $"T3 after 1.5s: firstCompleted=false, secondCompleted=false; results=Success,Success; rows={rows.Count}, qty={rows[0].Quantity}, audits={audits}");
        }
        finally
        {
            if (!blockerReleased)
            {
                await blockingTransaction.RollbackAsync();
            }

            if (firstTask is not null && secondTask is not null)
            {
                try
                {
                    await Task.WhenAll(firstTask, secondTask);
                }
                catch (OracleException)
                {
                    // 突變 P10 可能讓其中一筆撞唯一鍵；先等它結束再清資料，避免探針污染共用 DB。
                }
            }

            await CleanupItemAsync(itemId);
        }
    }

    [Fact]
    public async Task T4_item_lock_timeout_returns_explicit_result_without_writes_or_audit()
    {
        var itemId = await CreateItemAsync("T4");
        await using var blocker = new OracleConnection(OracleTestDatabase.ConnectionString);
        await blocker.OpenAsync();
        await using var blockingTransaction = await blocker.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        try
        {
            // 同 T3：先 await 並確認持鎖列，再開始會等待此鎖的入庫操作。
            var lockedItemId = await blocker.QuerySingleAsync<decimal>(
                "SELECT item_id FROM items WHERE item_id = :itemId FOR UPDATE",
                new { itemId },
                blockingTransaction);
            Assert.Equal(itemId, decimal.ToInt64(lockedItemId));

            await using var receivingConnection = new OracleConnection(OracleTestDatabase.ConnectionString);
            var service = new StockReceivingService(
                receivingConnection,
                new TestCurrentUser("itest-t4-keeper"));
            var result = await service.ReceiveAsync(
                itemId,
                "LOT-T4",
                new DateOnly(2031, 3, 1),
                5,
                "ROOM-T4",
                new DateOnly(2030, 1, 1),
                lockWaitSeconds: 1);

            Assert.Equal(ReceiveFailureReason.LockTimeout, result.FailureReason);
            await using var verify = new OracleConnection(OracleTestDatabase.ConnectionString);
            var lots = await verify.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM stock_lots WHERE item_id = :itemId",
                new { itemId });
            var audits = await verify.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM audit_logs WHERE entity_type = 'StockLot' AND actor = 'itest-t4-keeper'");
            Assert.Equal(0, lots);
            Assert.Equal(0, audits);
            _output.WriteLine($"T4 result={result.FailureReason}; stock_lots={lots}; receiveAudits={audits}");
        }
        finally
        {
            await blockingTransaction.RollbackAsync();
            await CleanupItemAsync(itemId);
        }
    }

    private static async Task<long> CreateItemAsync(string marker)
    {
        var code = $"IT-{marker}-{Guid.NewGuid():N}"[..24].ToUpperInvariant();
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.ExecuteAsync("""
            INSERT INTO items (item_code, item_name, unit_of_measure, safety_stock_qty, created_by)
            VALUES (:code, '整合測試品項', '盒', 0, :actor)
            """, new { code, actor = $"itest-{marker.ToLowerInvariant()}" });
        return await connection.QuerySingleAsync<long>(
            "SELECT item_id FROM items WHERE item_code = :code AND is_deleted = 0",
            new { code });
    }

    private static async Task<LotStateRow> ReadLotAsync(long stockLotId)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        return await connection.QuerySingleAsync<LotStateRow>("""
            SELECT expiry_date AS ExpiryDate,
                   quantity AS Quantity,
                   row_version AS RowVersion
            FROM stock_lots
            WHERE stock_lot_id = :stockLotId
            """, new { stockLotId });
    }

    private static async Task<int> CountReceiveAuditsAsync(long stockLotId)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        return await connection.QuerySingleAsync<int>("""
            SELECT COUNT(*)
            FROM audit_logs
            WHERE entity_type = 'StockLot'
              AND entity_id = TO_CHAR(:stockLotId)
              AND action = 'Receive'
            """, new { stockLotId });
    }

    private static async Task CleanupItemAsync(long itemId)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync("""
            DELETE FROM audit_logs
            WHERE entity_type = 'StockLot'
              AND entity_id IN (SELECT TO_CHAR(stock_lot_id) FROM stock_lots WHERE item_id = :itemId)
            """, new { itemId });
        await connection.ExecuteAsync("DELETE FROM stock_lots WHERE item_id = :itemId", new { itemId });
        await connection.ExecuteAsync("DELETE FROM items WHERE item_id = :itemId", new { itemId });
        await connection.ExecuteAsync("COMMIT");
    }

    private sealed class LotStateRow
    {
        public DateTime ExpiryDate { get; init; }
        public decimal Quantity { get; init; }
        public decimal RowVersion { get; init; }
    }

    private sealed class ConcurrentLotRow
    {
        public decimal StockLotId { get; init; }
        public decimal Quantity { get; init; }
    }

    private sealed class TestCurrentUser(string actor) : ICurrentUser
    {
        public string Actor { get; } = actor;
    }
}
