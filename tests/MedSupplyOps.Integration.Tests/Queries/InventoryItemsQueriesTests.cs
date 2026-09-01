using Dapper;
using MedSupplyOps.Infrastructure.Queries;
using Oracle.ManagedDataAccess.Client;

namespace MedSupplyOps.Integration.Tests.Queries;

/// <summary>驗證庫存頁品項摘要與既有單品項查詢採用相同的可用量定義。</summary>
public sealed class InventoryItemsQueriesTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    [Fact]
    public async Task GetInventoryItemsAsync_returns_all_seed_items_with_metadata_and_matching_availability()
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        var queries = new InventoryQueries(connection);

        var items = await queries.GetInventoryItemsAsync(Today);
        var seeded = items.Where(item => item.ItemCode.StartsWith("MD-", StringComparison.Ordinal)).ToList();

        Assert.Equal(5, seeded.Count);
        Assert.Equal(["MD-0001", "MD-0002", "MD-0003", "MD-0004", "MD-0005"], seeded.Select(item => item.ItemCode));
        Assert.All(seeded, item => Assert.False(string.IsNullOrWhiteSpace(item.Specification)));
        Assert.All(seeded, item => Assert.False(string.IsNullOrWhiteSpace(item.UnitOfMeasure)));

        var gloves = Assert.Single(seeded, item => item.ItemCode == "MD-0001");
        var availability = await queries.GetItemAvailabilityAsync(gloves.ItemId, Today);

        Assert.Equal(210, gloves.AvailableQuantity);
        Assert.Equal(availability.AvailableQuantity, gloves.AvailableQuantity);
        Assert.Equal(4, gloves.UsableLotCount);
    }
}
