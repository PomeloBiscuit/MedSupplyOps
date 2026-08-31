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
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    [Fact]
    public async Task GetItemAvailabilityAsync_sums_usable_lots_and_keeps_all_lot_details_in_FEFO_order()
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        var queries = new InventoryQueries(connection);
        var itemId = await connection.QuerySingleAsync<long>(
            "SELECT item_id FROM items WHERE item_code = :itemCode",
            new { itemCode = "MD-0001" });

        var result = await queries.GetItemAvailabilityAsync(itemId, Today);

        Assert.Equal(210, result.AvailableQuantity);
        Assert.Equal(
            ["GLO-EXPIRED-01", "GLO-FEFO-A", "GLO-FEFO-B", "GLO-FEFO-C", "GLO-FEFO-D"],
            result.Lots.Select(lot => lot.LotNumber));
        Assert.False(result.Lots[0].IsAvailable);
        Assert.All(result.Lots.Skip(1), lot => Assert.True(lot.IsAvailable));

        var lots = result.Lots
            .Where(lot => lot.IsAvailable)
            .Select(lot => new StockLot(
                lot.StockLotId,
                itemId,
                lot.LotNumber,
                lot.ExpiryDate,
                lot.Quantity,
                lot.StorageLocation));
        var expectedFefo = FefoAllocator.Allocate(lots, 210, Today);

        Assert.True(expectedFefo.IsSuccess);
        Assert.Equal(
            expectedFefo.Allocations.Select(allocation => allocation.LotNumber),
            result.Lots.Where(lot => lot.IsAvailable).Select(lot => lot.LotNumber));
    }

    [Fact]
    public async Task GetExpiringLotsAsync_returns_only_usable_lots_in_requested_window()
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        var queries = new InventoryQueries(connection);

        var result = await queries.GetExpiringLotsAsync(45, Today);

        // ★ 斷言只針對種子資料（品號 MD-*），不對「整個資料庫的內容」下斷言。
        //   這支查詢的語意本來就是「回傳所有 N 天內到期的批次」，
        //   所以其他測試或使用者新增的資料出現在結果裡是**正確行為**，不是缺陷。
        //   原本寫成整組相等，等於假設資料庫永遠只有種子資料 ——
        //   任何人加一筆近效期的批次都會讓它變紅，而紅的原因跟這支查詢無關。
        var seeded = result.Where(lot => lot.ItemCode.StartsWith("MD-", StringComparison.Ordinal)).ToList();
        Assert.Equal(
            ["GAUZE-01", "GLO-FEFO-A", "GLO-FEFO-B"],
            seeded.Select(lot => lot.LotNumber));

        // 這兩條仍然對「所有回傳結果」斷言 —— 它們檢查的是查詢自己的篩選條件，
        // 不論資料庫裡有什麼，回傳的每一筆都必須符合。
        Assert.All(result, lot => Assert.True(lot.Quantity > 0));
        Assert.All(result, lot => Assert.InRange(lot.ExpiryDate, Today, Today.AddDays(45)));
    }

    [Fact]
    public async Task GetItemsBelowSafetyStockAsync_counts_only_unexpired_nonzero_lots()
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        var queries = new InventoryQueries(connection);

        var result = await queries.GetItemsBelowSafetyStockAsync(Today);

        // 同上：只對種子資料斷言。這支查詢回傳的是全庫低於安全存量的品項，
        // 其他測試造的品項出現在裡面並不是缺陷。
        var seeded = result.Where(item => item.ItemCode.StartsWith("MD-", StringComparison.Ordinal)).ToList();
        Assert.Equal(["MD-0002", "MD-0003"], seeded.Select(item => item.ItemCode));
        Assert.Equal((100, 0), (seeded[0].SafetyStockQuantity, seeded[0].AvailableQuantity));
        Assert.Equal((60, 20), (seeded[1].SafetyStockQuantity, seeded[1].AvailableQuantity));
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
}
