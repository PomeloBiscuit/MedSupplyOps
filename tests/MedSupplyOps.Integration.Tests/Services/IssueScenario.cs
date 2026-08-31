using System.Data.Common;
using System.Globalization;
using Dapper;
using Oracle.ManagedDataAccess.Client;

namespace MedSupplyOps.Integration.Tests.Services;

/// <summary>
/// 發料測試的資料場景：建立一組獨立的科室／品項／請領單／批次，並在結束時刪乾淨。
///
/// 為什麼不用「包在交易裡再 rollback」那套（其他整合測試用的做法）：
/// <see cref="MedSupplyOps.Infrastructure.Services.StockIssueService"/> 自己會 COMMIT，
/// 而**並發行為的本質就是「跨 commit 之後會發生什麼」** ——
/// 在單一個會被 rollback 的交易裡，兩條連線根本看不到彼此，測不到任何東西。
/// 所以這裡改用「唯一後綴 + 明確刪除」，並在 finally 清理。
/// </summary>
internal sealed class IssueScenario : IAsyncDisposable
{
    private readonly string _suffix;

    private IssueScenario(string suffix)
    {
        _suffix = suffix;
    }

    public long ItemId { get; private set; }
    public long DepartmentId { get; private set; }

    /// <summary>每一條並發任務用一條自己的請領明細（issue_allocations 對同一明細+批次有唯一鍵）。</summary>
    public IReadOnlyList<long> RequisitionLineIds { get; private set; } = [];

    /// <summary>建立場景。<paramref name="lots"/> 是 (批號, 距今天數, 數量)。</summary>
    public static async Task<IssueScenario> CreateAsync(
        IReadOnlyList<(string LotNumber, int DaysFromToday, int Quantity)> lots,
        int requisitionLineCount = 1,
        CancellationToken cancellationToken = default)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var scenario = new IssueScenario(suffix);

        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO departments (department_code, department_name, created_by)
            VALUES (:code, :name, 'itest')
            """,
            new { code = "D" + suffix, name = "測試科室 " + suffix },
            cancellationToken: cancellationToken));
        scenario.DepartmentId = await ScalarIdAsync(connection,
            "SELECT department_id FROM departments WHERE department_code = :code",
            new { code = "D" + suffix }, cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO items (item_code, item_name, unit_of_measure, safety_stock_qty, created_by)
            VALUES (:code, :name, '個', 0, 'itest')
            """,
            new { code = "I" + suffix, name = "測試品項 " + suffix },
            cancellationToken: cancellationToken));
        scenario.ItemId = await ScalarIdAsync(connection,
            "SELECT item_id FROM items WHERE item_code = :code",
            new { code = "I" + suffix }, cancellationToken);

        foreach (var (lotNumber, daysFromToday, quantity) in lots)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO stock_lots
                    (item_id, lot_number, expiry_date, quantity, storage_location, created_by)
                VALUES
                    (:itemId, :lotNumber, TRUNC(SYSDATE) + :days, :qty, 'ITEST-A01', 'itest')
                """,
                new { itemId = scenario.ItemId, lotNumber, days = daysFromToday, qty = quantity },
                cancellationToken: cancellationToken));
        }

        var lineIds = new List<long>(requisitionLineCount);
        for (var i = 0; i < requisitionLineCount; i++)
        {
            var no = string.Create(CultureInfo.InvariantCulture, $"R{suffix}-{i:D3}");
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO requisitions (requisition_no, department_id, status, created_by)
                VALUES (:no, :deptId, 'Approved', 'itest')
                """,
                new { no, deptId = scenario.DepartmentId },
                cancellationToken: cancellationToken));

            var requisitionId = await ScalarIdAsync(connection,
                "SELECT requisition_id FROM requisitions WHERE requisition_no = :no",
                new { no }, cancellationToken);

            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO requisition_lines (requisition_id, line_no, item_id, quantity)
                VALUES (:reqId, 1, :itemId, 1)
                """,
                new { reqId = requisitionId, itemId = scenario.ItemId },
                cancellationToken: cancellationToken));

            lineIds.Add(await ScalarIdAsync(connection,
                "SELECT requisition_line_id FROM requisition_lines WHERE requisition_id = :reqId",
                new { reqId = requisitionId }, cancellationToken));
        }

        scenario.RequisitionLineIds = lineIds;
        return scenario;
    }

    /// <summary>讀回某批次目前的數量。用於斷言「資料庫裡真正的值」，而不是服務回報的值。</summary>
    public async Task<int> GetLotQuantityAsync(string lotNumber, CancellationToken cancellationToken = default)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var value = await connection.ExecuteScalarAsync<decimal?>(new CommandDefinition(
            "SELECT quantity FROM stock_lots WHERE item_id = :itemId AND lot_number = :lotNumber",
            new { itemId = ItemId, lotNumber },
            cancellationToken: cancellationToken));
        return value is null ? throw new InvalidOperationException($"找不到批次 {lotNumber}") : decimal.ToInt32(value.Value);
    }

    /// <summary>本場景所有批次的數量總和。用來斷言「總量守恆」。</summary>
    public async Task<int> GetTotalQuantityAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var value = await connection.ExecuteScalarAsync<decimal>(new CommandDefinition(
            "SELECT NVL(SUM(quantity), 0) FROM stock_lots WHERE item_id = :itemId",
            new { itemId = ItemId },
            cancellationToken: cancellationToken));
        return decimal.ToInt32(value);
    }

    /// <summary>本場景寫出的配批紀錄總量。應與成功發出的數量一致。</summary>
    public async Task<int> GetIssuedQuantityAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var value = await connection.ExecuteScalarAsync<decimal>(new CommandDefinition(
            """
            SELECT NVL(SUM(a.quantity), 0)
            FROM issue_allocations a
            JOIN stock_lots l ON l.stock_lot_id = a.stock_lot_id
            WHERE l.item_id = :itemId
            """,
            new { itemId = ItemId },
            cancellationToken: cancellationToken));
        return decimal.ToInt32(value);
    }

    public async Task<IReadOnlyList<(string LotNumber, int Quantity)>> GetIssuedByLotAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<(string LotNumber, decimal Quantity)>(new CommandDefinition(
            """
            SELECT l.lot_number AS LotNumber, SUM(a.quantity) AS Quantity
            FROM issue_allocations a
            JOIN stock_lots l ON l.stock_lot_id = a.stock_lot_id
            WHERE l.item_id = :itemId
            GROUP BY l.lot_number
            ORDER BY l.lot_number
            """,
            new { itemId = ItemId },
            cancellationToken: cancellationToken));
        return rows.Select(r => (r.LotNumber, decimal.ToInt32(r.Quantity))).ToList();
    }

    private static async Task<long> ScalarIdAsync(
        DbConnection connection, string sql, object parameters, CancellationToken cancellationToken)
    {
        var value = await connection.ExecuteScalarAsync<decimal?>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
        return value is null
            ? throw new InvalidOperationException($"查無資料：{sql}")
            : decimal.ToInt64(value.Value);
    }

    /// <summary>
    /// 依外鍵相依順序刪除本場景造出的所有資料。
    /// 刻意不使用 ON DELETE CASCADE —— schema 全域禁止 CASCADE（MIG-2），
    /// 測試的清理也不該是唯一的例外。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();

        var p = new { suffix = _suffix, itemId = ItemId, deptId = DepartmentId };

        await connection.ExecuteAsync("""
            DELETE FROM issue_allocations
            WHERE stock_lot_id IN (SELECT stock_lot_id FROM stock_lots WHERE item_id = :itemId)
            """, p);
        await connection.ExecuteAsync("""
            DELETE FROM requisition_lines
            WHERE requisition_id IN (SELECT requisition_id FROM requisitions WHERE requisition_no LIKE 'R' || :suffix || '%')
            """, p);
        await connection.ExecuteAsync(
            "DELETE FROM requisitions WHERE requisition_no LIKE 'R' || :suffix || '%'", p);
        await connection.ExecuteAsync("DELETE FROM stock_lots WHERE item_id = :itemId", p);
        await connection.ExecuteAsync("DELETE FROM items WHERE item_id = :itemId", p);
        await connection.ExecuteAsync("DELETE FROM departments WHERE department_id = :deptId", p);
        await connection.ExecuteAsync("COMMIT");
    }
}
