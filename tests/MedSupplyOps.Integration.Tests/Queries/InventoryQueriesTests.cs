using System.Data.Common;
using Dapper;
using MedSupplyOps.Domain.Inventory;
using MedSupplyOps.Infrastructure.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Oracle.ManagedDataAccess.Client;

namespace MedSupplyOps.Integration.Tests.Queries;

/// <summary>以真 Oracle 驗證 Dapper 讀取查詢的答案、FEFO 次序及效期邊界。</summary>
public sealed class InventoryQueriesTests
{
    // ★ 查的是種子資料（MD-*），效期是相對於建庫當天算的 —— 用真實業務日期，不用固定的假日期。見 L-026。
    private static readonly DateOnly Today = TestBusinessCalendar.SystemToday;

    [Fact]
    public async Task GetItemAvailabilityAsync_sums_usable_lots_and_keeps_all_lot_details_in_FEFO_order()
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        var queries = new InventoryQueries(connection);
        var itemId = await connection.QuerySingleAsync<long>(
            "SELECT item_id FROM items WHERE item_code = :itemCode",
            new { itemCode = "MD-0001" });

        var result = await queries.GetItemAvailabilityAsync(itemId, Today);
        var expectedAvailableQuantity = await connection.QuerySingleAsync<decimal>(
            """
            SELECT NVL(SUM(CASE
                       WHEN l.expiry_date >= :asOf AND l.quantity > 0 THEN l.quantity
                       ELSE 0
                   END), 0)
            FROM items i
            LEFT JOIN stock_lots l ON l.item_id = i.item_id
            WHERE i.item_id = :itemId
              AND i.is_deleted = 0
            """,
            new { itemId, asOf = Today.ToDateTime(TimeOnly.MinValue) });
        var expectedLotNumbers = await connection.QueryAsync<string>(
            """
            SELECT lot_number
            FROM stock_lots
            WHERE item_id = :itemId
            ORDER BY expiry_date, lot_number, stock_lot_id
            """,
            new { itemId });

        Assert.Equal(decimal.ToInt32(expectedAvailableQuantity), result.AvailableQuantity);
        Assert.Equal(expectedLotNumbers, result.Lots.Select(lot => lot.LotNumber));
        Assert.All(
            result.Lots,
            lot => Assert.Equal(lot.Quantity > 0 && lot.ExpiryDate >= Today, lot.IsAvailable));
    }

    [Fact]
    public async Task GetExpiringLotsAsync_returns_only_usable_lots_in_requested_window()
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        var queries = new InventoryQueries(connection);

        var result = await queries.GetExpiringLotsAsync(45, Today);
        var expectedLotIds = await connection.QueryAsync<decimal>(
            """
            SELECT l.stock_lot_id
            FROM stock_lots l
            INNER JOIN items i ON i.item_id = l.item_id
            WHERE i.is_deleted = 0
              AND l.quantity > 0
              AND l.expiry_date >= :asOf
              AND l.expiry_date <= :expiresBy
            ORDER BY l.expiry_date, l.lot_number, l.stock_lot_id
            """,
            new
            {
                asOf = Today.ToDateTime(TimeOnly.MinValue),
                expiresBy = Today.AddDays(45).ToDateTime(TimeOnly.MinValue),
            });

        Assert.Equal(expectedLotIds.Select(decimal.ToInt64), result.Select(lot => lot.StockLotId));
        Assert.All(result, lot => Assert.True(lot.Quantity > 0));
        Assert.All(result, lot => Assert.InRange(lot.ExpiryDate, Today, Today.AddDays(45)));
    }

    [Fact]
    public async Task GetItemsBelowSafetyStockAsync_counts_only_unexpired_nonzero_lots()
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        var queries = new InventoryQueries(connection);

        var result = await queries.GetItemsBelowSafetyStockAsync(Today);
        var expected = (await connection.QueryAsync<ExpectedLowStockItem>(
            """
            SELECT i.item_id AS ItemId,
                   i.item_code AS ItemCode,
                   i.safety_stock_qty AS SafetyStockQuantity,
                   NVL(SUM(CASE
                       WHEN l.expiry_date >= :asOf AND l.quantity > 0 THEN l.quantity
                       ELSE 0
                   END), 0) AS AvailableQuantity
            FROM items i
            LEFT JOIN stock_lots l ON l.item_id = i.item_id
            WHERE i.is_deleted = 0
              AND i.item_code LIKE 'MD-%'
            GROUP BY i.item_id, i.item_code, i.safety_stock_qty
            HAVING NVL(SUM(CASE
                       WHEN l.expiry_date >= :asOf AND l.quantity > 0 THEN l.quantity
                       ELSE 0
                   END), 0) < i.safety_stock_qty
            ORDER BY i.item_code, i.item_id
            """,
            new { asOf = Today.ToDateTime(TimeOnly.MinValue) })).AsList();
        var actual = result.Where(item => item.ItemCode.StartsWith("MD-", StringComparison.Ordinal)).ToList();

        Assert.Equal(
            expected.Select(item => (
                decimal.ToInt64(item.ItemId),
                item.ItemCode,
                decimal.ToInt32(item.SafetyStockQuantity),
                decimal.ToInt32(item.AvailableQuantity))),
            actual.Select(item => (
                item.ItemId,
                item.ItemCode,
                item.SafetyStockQuantity,
                item.AvailableQuantity)));
    }

    [Fact]
    public async Task GetItemAvailabilityAsync_treats_a_lot_expiring_on_asOf_as_available()
    {
        await using var context = OracleTestDatabase.CreateContext();
        await context.Database.OpenConnectionAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();

        var item = new MedSupplyOps.Infrastructure.Persistence.Models.Item
        {
            Code = "QUERY-BOUNDARY-" + Guid.NewGuid().ToString("N")[..12],
            Name = "效期邊界測試品項",
            UnitOfMeasure = "件",
        };
        context.Items.Add(item);
        await context.SaveChangesAsync();
        context.StockLots.Add(new StockLot(0, item.Id, "BOUNDARY-LOT", Today, 7, "TEST-A01"));
        await context.SaveChangesAsync();

        var queries = new InventoryQueries(context.Database.GetDbConnection());
        var result = await queries.GetItemAvailabilityAsync(item.Id, Today, GetDbTransaction(transaction));

        Assert.Equal(7, result.AvailableQuantity);
        var lot = Assert.Single(result.Lots);
        Assert.Equal(Today, lot.ExpiryDate);
        Assert.True(lot.IsAvailable);

        await transaction.RollbackAsync();
        await context.Database.CloseConnectionAsync();
    }

    private static DbTransaction GetDbTransaction(IDbContextTransaction transaction)
        => transaction.GetDbTransaction();

    private sealed class ExpectedLowStockItem
    {
        public decimal ItemId { get; set; }
        public string ItemCode { get; set; } = string.Empty;
        public decimal SafetyStockQuantity { get; set; }
        public decimal AvailableQuantity { get; set; }
    }
}
