using System.Data.Common;
using Dapper;

namespace MedSupplyOps.Infrastructure.Queries;

/// <summary>
/// 庫存讀取路徑。所有資料都以 Dapper 執行明確的 Oracle SQL，不經 EF Core 轉譯。
/// </summary>
public sealed class InventoryQueries
{
    private readonly DbConnection _connection;

    /// <summary>
    /// 使用呼叫端依既有 <c>ConnectionStrings:MedSupplyOps</c> 組態建立的連線。
    /// 連線與交易的生命週期仍由呼叫端管理，讓整合測試可在 rollback 的交易中驗證查詢。
    /// </summary>
    public InventoryQueries(DbConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    /// <summary>
    /// 取得一個品項的可用量與所有批次明細。可用量只加總未過期且數量大於零的批次；
    /// 明細仍保留這些不可用批次並明確標示。
    /// </summary>
    public async Task<ItemAvailability> GetItemAvailabilityAsync(
        long itemId,
        DateOnly asOf,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT stock_lot_id AS StockLotId,
                   lot_number AS LotNumber,
                   expiry_date AS ExpiryDate,
                   quantity AS Quantity,
                   storage_location AS StorageLocation,
                   CASE
                       WHEN expiry_date >= :asOf AND quantity > 0 THEN 1
                       ELSE 0
                   END AS IsAvailable,
                   NVL(SUM(CASE
                WHEN expiry_date >= :asOf AND quantity > 0 THEN quantity
                ELSE 0
            END) OVER (), 0) AS AvailableQuantity
            FROM stock_lots
            WHERE item_id = :itemId
            ORDER BY expiry_date, lot_number, stock_lot_id
            """;

        await EnsureOpenAsync(cancellationToken);
        var asOfDate = asOf.ToDateTime(TimeOnly.MinValue);
        var command = new CommandDefinition(
            sql,
            new { itemId, asOf = asOfDate },
            transaction,
            cancellationToken: cancellationToken);
        var rows = (await _connection.QueryAsync<ItemAvailabilityLotRow>(command)).AsList();
        var lots = rows
            .Select(row => new ItemAvailabilityLot(
                decimal.ToInt64(row.StockLotId),
                row.LotNumber,
                DateOnly.FromDateTime(row.ExpiryDate),
                row.Quantity,
                row.StorageLocation,
                row.IsAvailable != 0m))
            .ToList();

        return new ItemAvailability(itemId, rows.Count == 0 ? 0 : decimal.ToInt32(rows[0].AvailableQuantity), lots);
    }

    /// <summary>取得從 <paramref name="asOf"/> 起（含）指定天數內到期且仍有數量的批次。</summary>
    public async Task<IReadOnlyList<ExpiringLot>> GetExpiringLotsAsync(
        int withinDays,
        DateOnly asOf,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        if (withinDays < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(withinDays), withinDays, "天數不得為負數。");
        }

        const string sql = """
            SELECT l.stock_lot_id AS StockLotId,
                   l.item_id AS ItemId,
                   i.item_code AS ItemCode,
                   i.item_name AS ItemName,
                   l.lot_number AS LotNumber,
                   l.expiry_date AS ExpiryDate,
                   l.quantity AS Quantity,
                   l.storage_location AS StorageLocation
            FROM stock_lots l
            INNER JOIN items i ON i.item_id = l.item_id
            WHERE i.is_deleted = 0
              AND l.quantity > 0
              AND l.expiry_date >= :asOf
              AND l.expiry_date <= :expiresBy
            ORDER BY l.expiry_date, l.lot_number, l.stock_lot_id
            """;

        var asOfDate = asOf.ToDateTime(TimeOnly.MinValue);
        var expiresBy = asOf.AddDays(withinDays).ToDateTime(TimeOnly.MinValue);
        await EnsureOpenAsync(cancellationToken);
        var command = new CommandDefinition(
            sql,
            new { asOf = asOfDate, expiresBy },
            transaction,
            cancellationToken: cancellationToken);
        return (await _connection.QueryAsync<ExpiringLotRow>(command))
            .Select(row => new ExpiringLot(
                decimal.ToInt64(row.StockLotId),
                decimal.ToInt64(row.ItemId),
                row.ItemCode,
                row.ItemName,
                row.LotNumber,
                DateOnly.FromDateTime(row.ExpiryDate),
                row.Quantity,
                row.StorageLocation))
            .ToList();
    }

    /// <summary>取得未過期可用量嚴格低於安全存量的未刪除品項。</summary>
    public async Task<IReadOnlyList<ItemBelowSafetyStock>> GetItemsBelowSafetyStockAsync(
        DateOnly asOf,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT i.item_id AS ItemId,
                   i.item_code AS ItemCode,
                   i.item_name AS ItemName,
                   i.safety_stock_qty AS SafetyStockQuantity,
                   NVL(SUM(CASE
                       WHEN l.expiry_date >= :asOf AND l.quantity > 0 THEN l.quantity
                       ELSE 0
                   END), 0) AS AvailableQuantity
            FROM items i
            LEFT JOIN stock_lots l ON l.item_id = i.item_id
            WHERE i.is_deleted = 0
            GROUP BY i.item_id, i.item_code, i.item_name, i.safety_stock_qty
            HAVING NVL(SUM(CASE
                WHEN l.expiry_date >= :asOf AND l.quantity > 0 THEN l.quantity
                ELSE 0
            END), 0) < i.safety_stock_qty
            ORDER BY i.item_code, i.item_id
            """;

        await EnsureOpenAsync(cancellationToken);
        var asOfDate = asOf.ToDateTime(TimeOnly.MinValue);
        var command = new CommandDefinition(sql, new { asOf = asOfDate }, transaction, cancellationToken: cancellationToken);
        return (await _connection.QueryAsync<ItemBelowSafetyStockRow>(command))
            .Select(row => new ItemBelowSafetyStock(
                decimal.ToInt64(row.ItemId),
                row.ItemCode,
                row.ItemName,
                decimal.ToInt32(row.SafetyStockQuantity),
                decimal.ToInt32(row.AvailableQuantity)))
            .ToList();
    }

    private async Task EnsureOpenAsync(CancellationToken cancellationToken)
    {
        if (_connection.State == System.Data.ConnectionState.Open)
        {
            return;
        }

        await _connection.OpenAsync(cancellationToken);
    }
}

internal sealed class ItemAvailabilityLotRow
{
    public decimal StockLotId { get; set; }
    public string LotNumber { get; set; } = string.Empty;
    public DateTime ExpiryDate { get; set; }
    public int Quantity { get; set; }
    public string StorageLocation { get; set; } = string.Empty;
    public decimal IsAvailable { get; set; }
    public decimal AvailableQuantity { get; set; }
}

internal sealed class ExpiringLotRow
{
    public decimal StockLotId { get; set; }
    public decimal ItemId { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public string LotNumber { get; set; } = string.Empty;
    public DateTime ExpiryDate { get; set; }
    public int Quantity { get; set; }
    public string StorageLocation { get; set; } = string.Empty;
}

internal sealed class ItemBelowSafetyStockRow
{
    public decimal ItemId { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public decimal SafetyStockQuantity { get; set; }
    public decimal AvailableQuantity { get; set; }
}

/// <summary>品項庫存總覽。</summary>
public sealed record ItemAvailability(long ItemId, int AvailableQuantity, IReadOnlyList<ItemAvailabilityLot> Lots);

/// <summary>品項庫存總覽中的單一批次；<see cref="IsAvailable"/> 供畫面標示不可用批次。</summary>
public sealed record ItemAvailabilityLot(
    long StockLotId,
    string LotNumber,
    DateOnly ExpiryDate,
    int Quantity,
    string StorageLocation,
    bool IsAvailable);

/// <summary>效期預警中的單一批次。</summary>
public sealed record ExpiringLot(
    long StockLotId,
    long ItemId,
    string ItemCode,
    string ItemName,
    string LotNumber,
    DateOnly ExpiryDate,
    int Quantity,
    string StorageLocation);

/// <summary>低庫存預警中的單一品項。</summary>
public sealed record ItemBelowSafetyStock(
    long ItemId,
    string ItemCode,
    string ItemName,
    int SafetyStockQuantity,
    int AvailableQuantity);
