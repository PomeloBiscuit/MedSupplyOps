using Dapper;
using MedSupplyOps.Infrastructure.Queries;
using Oracle.ManagedDataAccess.Client;

namespace MedSupplyOps.Integration.Tests.Queries;

/// <summary>驗證庫存頁品項摘要與既有單品項查詢採用相同的可用量定義。</summary>
public sealed class InventoryItemsQueriesTests
{
    // ★ 查的是種子資料（MD-*），效期是相對於建庫當天算的 —— 用真實業務日期，不用固定的假日期。
    private static readonly DateOnly Today = TestBusinessCalendar.SystemToday;

    [Fact]
    public async Task GetInventoryItemsAsync_returns_all_seed_items_with_metadata_and_matching_availability()
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        var queries = new InventoryQueries(connection);

        var items = await queries.GetInventoryItemsAsync(Today);
        var seeded = items.Where(item => item.ItemCode.StartsWith("MD-", StringComparison.Ordinal)).ToList();
        var expected = (await connection.QueryAsync<ExpectedInventoryItem>(
            """
            SELECT i.item_id AS ItemId,
                   i.item_code AS ItemCode,
                   i.specification AS Specification,
                   i.unit_of_measure AS UnitOfMeasure,
                   NVL(SUM(CASE
                       WHEN l.expiry_date >= :asOf AND l.quantity > 0 THEN l.quantity
                       ELSE 0
                   END), 0) AS AvailableQuantity,
                   NVL(SUM(CASE
                       WHEN l.expiry_date >= :asOf AND l.quantity > 0 THEN 1
                       ELSE 0
                   END), 0) AS UsableLotCount
            FROM items i
            LEFT JOIN stock_lots l ON l.item_id = i.item_id
            WHERE i.is_deleted = 0
              AND i.item_code LIKE 'MD-%'
            GROUP BY i.item_id, i.item_code, i.specification, i.unit_of_measure
            ORDER BY i.item_code, i.item_id
            """,
            new { asOf = Today.ToDateTime(TimeOnly.MinValue) })).AsList();

        Assert.Equal(
            expected.Select(item => (
                decimal.ToInt64(item.ItemId),
                item.ItemCode,
                item.Specification,
                item.UnitOfMeasure,
                decimal.ToInt32(item.AvailableQuantity),
                decimal.ToInt32(item.UsableLotCount))),
            seeded.Select(item => (
                item.ItemId,
                item.ItemCode,
                item.Specification,
                item.UnitOfMeasure,
                item.AvailableQuantity,
                item.UsableLotCount)));
    }

    private sealed class ExpectedInventoryItem
    {
        public decimal ItemId { get; set; }
        public string ItemCode { get; set; } = string.Empty;
        public string? Specification { get; set; }
        public string UnitOfMeasure { get; set; } = string.Empty;
        public decimal AvailableQuantity { get; set; }
        public decimal UsableLotCount { get; set; }
    }
}
