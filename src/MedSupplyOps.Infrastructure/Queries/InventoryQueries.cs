using System.Data.Common;
using System.Globalization;
using Dapper;
using MedSupplyOps.Infrastructure.Localization;

namespace MedSupplyOps.Infrastructure.Queries;

/// <summary>
/// 庫存讀取路徑。所有資料都以 Dapper 執行明確的 Oracle SQL，不經 EF Core 轉譯。
/// </summary>
public sealed class InventoryQueries
{
    // 可用量、效期預警與品項清單必須用同一個定義；{0} 是 SQL 批次表別名。
    private const string UsableLotPredicate = "{0}.expiry_date >= :asOf AND {0}.quantity > 0";

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
        var usableLotPredicate = UsableLotPredicate.Replace("{0}", "l", StringComparison.Ordinal);
        const string sqlTemplate = """
            SELECT l.stock_lot_id AS StockLotId,
                   l.lot_number AS LotNumber,
                   l.expiry_date AS ExpiryDate,
                   l.quantity AS Quantity,
                   l.storage_location AS StorageLocation,
                   CASE
                       WHEN {usableLotPredicate} THEN 1
                       ELSE 0
                   END AS IsAvailable,
                   NVL(SUM(CASE
                WHEN {usableLotPredicate} THEN l.quantity
                ELSE 0
            END) OVER (), 0) AS AvailableQuantity
            FROM stock_lots l
            WHERE l.item_id = :itemId
            ORDER BY l.expiry_date, l.lot_number, l.stock_lot_id
            """;

        var sql = sqlTemplate.Replace("{usableLotPredicate}", usableLotPredicate, StringComparison.Ordinal);

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

        var usableLotPredicate = UsableLotPredicate.Replace("{0}", "l", StringComparison.Ordinal);
        const string sqlTemplate = """
            SELECT l.stock_lot_id AS StockLotId,
                   l.item_id AS ItemId,
                   i.item_code AS ItemCode,
                   i.item_name AS ItemName,
                   i.item_name_en AS ItemNameEn,
                   l.lot_number AS LotNumber,
                   l.expiry_date AS ExpiryDate,
                   l.quantity AS Quantity,
                   l.storage_location AS StorageLocation
            FROM stock_lots l
            INNER JOIN items i ON i.item_id = l.item_id
            WHERE i.is_deleted = 0
              AND {usableLotPredicate}
              AND l.expiry_date <= :expiresBy
            ORDER BY l.expiry_date, l.lot_number, l.stock_lot_id
            """;

        var sql = sqlTemplate.Replace("{usableLotPredicate}", usableLotPredicate, StringComparison.Ordinal);

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
                row.StorageLocation,
                row.ItemNameEn))
            .ToList();
    }

    /// <summary>取得未過期可用量嚴格低於安全存量的未刪除品項。</summary>
    public async Task<IReadOnlyList<ItemBelowSafetyStock>> GetItemsBelowSafetyStockAsync(
        DateOnly asOf,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        var usableLotPredicate = UsableLotPredicate.Replace("{0}", "l", StringComparison.Ordinal);
        const string sqlTemplate = """
            SELECT i.item_id AS ItemId,
                   i.item_code AS ItemCode,
                   i.item_name AS ItemName,
                   i.item_name_en AS ItemNameEn,
                   i.safety_stock_qty AS SafetyStockQuantity,
                   NVL(SUM(CASE
                       WHEN {usableLotPredicate} THEN l.quantity
                       ELSE 0
                   END), 0) AS AvailableQuantity
            FROM items i
            LEFT JOIN stock_lots l ON l.item_id = i.item_id
            WHERE i.is_deleted = 0
            GROUP BY i.item_id, i.item_code, i.item_name, i.item_name_en, i.safety_stock_qty
            HAVING NVL(SUM(CASE
                WHEN {usableLotPredicate} THEN l.quantity
                ELSE 0
            END), 0) < i.safety_stock_qty
            ORDER BY i.item_code, i.item_id
            """;

        var sql = sqlTemplate.Replace("{usableLotPredicate}", usableLotPredicate, StringComparison.Ordinal);

        await EnsureOpenAsync(cancellationToken);
        var asOfDate = asOf.ToDateTime(TimeOnly.MinValue);
        var command = new CommandDefinition(sql, new { asOf = asOfDate }, transaction, cancellationToken: cancellationToken);
        return (await _connection.QueryAsync<ItemBelowSafetyStockRow>(command))
            .Select(row => new ItemBelowSafetyStock(
                decimal.ToInt64(row.ItemId),
                row.ItemCode,
                row.ItemName,
                decimal.ToInt32(row.SafetyStockQuantity),
                decimal.ToInt32(row.AvailableQuantity),
                row.ItemNameEn))
            .ToList();
    }

    /// <summary>
    /// 列舉未軟刪除品項及其依 <paramref name="asOf"/> 計算的可用庫存摘要。
    /// 無批次的品項仍會回傳，讓畫面能顯示可用量為零。
    /// </summary>
    public async Task<IReadOnlyList<InventoryItem>> GetInventoryItemsAsync(
        DateOnly asOf,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        var usableLotPredicate = UsableLotPredicate.Replace("{0}", "l", StringComparison.Ordinal);
        const string sqlTemplate = """
            SELECT i.item_id AS ItemId,
                   i.item_code AS ItemCode,
                   i.item_name AS ItemName,
                   i.item_name_en AS ItemNameEn,
                   i.specification AS Specification,
                   i.specification_en AS SpecificationEn,
                   i.unit_of_measure AS UnitOfMeasure,
                   i.unit_of_measure_en AS UnitOfMeasureEn,
                   i.safety_stock_qty AS SafetyStockQuantity,
                   NVL(SUM(CASE
                       WHEN {usableLotPredicate} THEN l.quantity
                       ELSE 0
                   END), 0) AS AvailableQuantity,
                   NVL(SUM(CASE
                       WHEN {usableLotPredicate} THEN 1
                       ELSE 0
                   END), 0) AS UsableLotCount,
                   MIN(CASE
                       WHEN {usableLotPredicate} THEN l.expiry_date
                   END) AS EarliestUsableExpiry
            FROM items i
            LEFT JOIN stock_lots l ON l.item_id = i.item_id
            WHERE i.is_deleted = 0
            GROUP BY i.item_id,
                     i.item_code,
                     i.item_name,
                     i.item_name_en,
                     i.specification,
                     i.specification_en,
                     i.unit_of_measure,
                     i.unit_of_measure_en,
                     i.safety_stock_qty
            ORDER BY i.item_code, i.item_id
            """;
        var sql = sqlTemplate.Replace("{usableLotPredicate}", usableLotPredicate, StringComparison.Ordinal);

        await EnsureOpenAsync(cancellationToken);
        var asOfDate = asOf.ToDateTime(TimeOnly.MinValue);
        var command = new CommandDefinition(sql, new { asOf = asOfDate }, transaction, cancellationToken: cancellationToken);
        return (await _connection.QueryAsync<InventoryItemRow>(command))
            .Select(row => new InventoryItem(
                decimal.ToInt64(row.ItemId),
                row.ItemCode,
                row.ItemName,
                row.Specification,
                row.UnitOfMeasure,
                decimal.ToInt32(row.SafetyStockQuantity),
                decimal.ToInt32(row.AvailableQuantity),
                decimal.ToInt32(row.UsableLotCount),
                row.EarliestUsableExpiry is null ? null : DateOnly.FromDateTime(row.EarliestUsableExpiry.Value),
                row.ItemNameEn,
                row.SpecificationEn,
                row.UnitOfMeasureEn))
            .ToList();
    }

    /// <summary>取得請領單已發料明細的配批紀錄，供畫面追溯實際批號與發料當下效期。</summary>
    public async Task<IReadOnlyList<RequisitionIssueAllocation>> GetRequisitionIssueAllocationsAsync(
        long requisitionId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT rl.line_no AS LineNo,
                   i.item_code AS ItemCode,
                   i.item_name AS ItemName,
                   i.item_name_en AS ItemNameEn,
                   i.unit_of_measure AS UnitOfMeasure,
                   i.unit_of_measure_en AS UnitOfMeasureEn,
                   l.lot_number AS LotNumber,
                   a.expiry_date_at_issue AS ExpiryDate,
                   a.quantity AS Quantity
            FROM issue_allocations a
            INNER JOIN requisition_lines rl ON rl.requisition_line_id = a.requisition_line_id
            INNER JOIN items i ON i.item_id = rl.item_id
            INNER JOIN stock_lots l ON l.stock_lot_id = a.stock_lot_id
            WHERE rl.requisition_id = :requisitionId
            ORDER BY rl.line_no, a.expiry_date_at_issue, l.lot_number, l.stock_lot_id
            """;

        await EnsureOpenAsync(cancellationToken);
        var command = new CommandDefinition(sql, new { requisitionId }, cancellationToken: cancellationToken);
        return (await _connection.QueryAsync<RequisitionIssueAllocationRow>(command))
            .Select(row => new RequisitionIssueAllocation(
                decimal.ToInt32(row.LineNo),
                row.ItemCode,
                row.ItemName,
                row.UnitOfMeasure,
                row.LotNumber,
                DateOnly.FromDateTime(row.ExpiryDate),
                row.Quantity,
                row.ItemNameEn,
                row.UnitOfMeasureEn))
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
    public string? ItemNameEn { get; set; }
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
    public string? ItemNameEn { get; set; }
    public decimal SafetyStockQuantity { get; set; }
    public decimal AvailableQuantity { get; set; }
}

internal sealed class InventoryItemRow
{
    public decimal ItemId { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public string? ItemNameEn { get; set; }
    public string? Specification { get; set; }
    public string? SpecificationEn { get; set; }
    public string UnitOfMeasure { get; set; } = string.Empty;
    public string? UnitOfMeasureEn { get; set; }
    public decimal SafetyStockQuantity { get; set; }
    public decimal AvailableQuantity { get; set; }
    public decimal UsableLotCount { get; set; }
    public DateTime? EarliestUsableExpiry { get; set; }
}

internal sealed class RequisitionIssueAllocationRow
{
    public decimal LineNo { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public string? ItemNameEn { get; set; }
    public string UnitOfMeasure { get; set; } = string.Empty;
    public string? UnitOfMeasureEn { get; set; }
    public string LotNumber { get; set; } = string.Empty;
    public DateTime ExpiryDate { get; set; }
    public int Quantity { get; set; }
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
    string StorageLocation,
    string? ItemNameEn)
{
    public ExpiringLot ForCulture(CultureInfo culture) => this with
    {
        ItemName = BilingualText.Resolve(ItemName, ItemNameEn, culture) ?? ItemName,
    };
}

/// <summary>低庫存預警中的單一品項。</summary>
public sealed record ItemBelowSafetyStock(
    long ItemId,
    string ItemCode,
    string ItemName,
    int SafetyStockQuantity,
    int AvailableQuantity,
    string? ItemNameEn)
{
    public ItemBelowSafetyStock ForCulture(CultureInfo culture) => this with
    {
        ItemName = BilingualText.Resolve(ItemName, ItemNameEn, culture) ?? ItemName,
    };
}

/// <summary>庫存查詢頁使用的品項摘要。</summary>
public sealed record InventoryItem(
    long ItemId,
    string ItemCode,
    string ItemName,
    string? Specification,
    string UnitOfMeasure,
    int SafetyStockQuantity,
    int AvailableQuantity,
    int UsableLotCount,
    DateOnly? EarliestUsableExpiry,
    string? ItemNameEn,
    string? SpecificationEn,
    string? UnitOfMeasureEn)
{
    public InventoryItem ForCulture(CultureInfo culture) => this with
    {
        ItemName = BilingualText.Resolve(ItemName, ItemNameEn, culture) ?? ItemName,
        Specification = BilingualText.Resolve(Specification, SpecificationEn, culture),
        UnitOfMeasure = BilingualText.Resolve(UnitOfMeasure, UnitOfMeasureEn, culture) ?? UnitOfMeasure,
    };
}

/// <summary>請領單畫面用的已發料配批紀錄。</summary>
public sealed record RequisitionIssueAllocation(
    int LineNo,
    string ItemCode,
    string ItemName,
    string UnitOfMeasure,
    string LotNumber,
    DateOnly ExpiryDate,
    int Quantity,
    string? ItemNameEn,
    string? UnitOfMeasureEn)
{
    public RequisitionIssueAllocation ForCulture(CultureInfo culture) => this with
    {
        ItemName = BilingualText.Resolve(ItemName, ItemNameEn, culture) ?? ItemName,
        UnitOfMeasure = BilingualText.Resolve(UnitOfMeasure, UnitOfMeasureEn, culture) ?? UnitOfMeasure,
    };
}
