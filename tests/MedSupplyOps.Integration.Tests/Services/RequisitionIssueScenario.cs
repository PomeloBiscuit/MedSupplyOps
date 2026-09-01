using System.Globalization;
using Dapper;
using MedSupplyOps.Domain.Requisitions;
using Oracle.ManagedDataAccess.Client;

namespace MedSupplyOps.Integration.Tests.Services;

/// <summary>一個品項的規格：品項代號、它的批次、以及這張單要領多少。</summary>
internal sealed record RequisitionItemSpec(
    string ItemKey,
    IReadOnlyList<(string LotNumber, int DaysFromToday, int Quantity)> Lots,
    int RequestQuantity);

/// <summary>
/// 整張請領單發料測試的資料場景：一張單、多個品項、每個品項一到多個批次。
///
/// 與 <see cref="IssueScenario"/> 的差別：那個是「多張單搶同一個品項」（測並發），
/// 這個是「一張單含多個品項」（測原子性）。兩種形狀的測試需求不同，不共用。
///
/// 一樣不能靠交易 rollback 清資料 —— 受測方法自己會 COMMIT。
/// 用唯一後綴 + 依外鍵順序明確刪除。
/// </summary>
internal sealed class RequisitionIssueScenario : IAsyncDisposable
{
    private readonly string _suffix;
    private readonly Dictionary<string, long> _itemIds = [];

    private RequisitionIssueScenario(string suffix) => _suffix = suffix;

    public long RequisitionId { get; private set; }
    public long DepartmentId { get; private set; }

    public long ItemIdOf(string itemKey) => _itemIds[itemKey];

    public static async Task<RequisitionIssueScenario> CreateAsync(
        IReadOnlyList<RequisitionItemSpec> items,
        RequisitionStatus status = RequisitionStatus.Approved,
        CancellationToken cancellationToken = default)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var scenario = new RequisitionIssueScenario(suffix);

        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO departments (department_code, department_name, created_by)
            VALUES (:code, :name, 'itest')
            """,
            new { code = "D" + suffix, name = "測試科室 " + suffix },
            cancellationToken: cancellationToken));
        scenario.DepartmentId = await ScalarAsync(connection,
            "SELECT department_id FROM departments WHERE department_code = :code",
            new { code = "D" + suffix }, cancellationToken);

        var requisitionNo = "R" + suffix;
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO requisitions (requisition_no, department_id, status, created_by)
            VALUES (:no, :deptId, :status, 'itest')
            """,
            new { no = requisitionNo, deptId = scenario.DepartmentId, status = status.ToString() },
            cancellationToken: cancellationToken));
        scenario.RequisitionId = await ScalarAsync(connection,
            "SELECT requisition_id FROM requisitions WHERE requisition_no = :no",
            new { no = requisitionNo }, cancellationToken);

        var lineNo = 1;
        foreach (var spec in items)
        {
            var itemCode = string.Create(CultureInfo.InvariantCulture, $"I{suffix}{spec.ItemKey}");
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO items (item_code, item_name, unit_of_measure, safety_stock_qty, created_by)
                VALUES (:code, :name, '個', 0, 'itest')
                """,
                new { code = itemCode, name = "測試品項 " + spec.ItemKey },
                cancellationToken: cancellationToken));

            var itemId = await ScalarAsync(connection,
                "SELECT item_id FROM items WHERE item_code = :code",
                new { code = itemCode }, cancellationToken);
            scenario._itemIds[spec.ItemKey] = itemId;

            foreach (var (lotNumber, days, quantity) in spec.Lots)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO stock_lots
                        (item_id, lot_number, expiry_date, quantity, storage_location, created_by)
                    VALUES
                        (:itemId, :lotNumber, TRUNC(SYSDATE) + :days, :qty, 'ITEST-A01', 'itest')
                    """,
                    new { itemId, lotNumber, days, qty = quantity },
                    cancellationToken: cancellationToken));
            }

            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO requisition_lines (requisition_id, line_no, item_id, quantity)
                VALUES (:reqId, :lineNo, :itemId, :qty)
                """,
                new { reqId = scenario.RequisitionId, lineNo, itemId, qty = spec.RequestQuantity },
                cancellationToken: cancellationToken));
            lineNo++;
        }

        return scenario;
    }

    public async Task<int> GetLotQuantityAsync(string lotNumber, CancellationToken cancellationToken = default)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var value = await connection.ExecuteScalarAsync<decimal?>(new CommandDefinition(
            """
            SELECT l.quantity FROM stock_lots l
            JOIN items i ON i.item_id = l.item_id
            WHERE i.item_code LIKE 'I' || :suffix || '%' AND l.lot_number = :lotNumber
            """,
            new { suffix = _suffix, lotNumber },
            cancellationToken: cancellationToken));
        return value is null ? throw new InvalidOperationException($"找不到批次 {lotNumber}") : decimal.ToInt32(value.Value);
    }

    public async Task<RequisitionStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var value = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT status FROM requisitions WHERE requisition_id = :id",
            new { id = RequisitionId },
            cancellationToken: cancellationToken));
        return Enum.Parse<RequisitionStatus>(value!);
    }

    /// <summary>本場景寫出的配批紀錄總量。原子性測試靠它斷言「一筆都沒留下」。</summary>
    public async Task<int> GetIssuedQuantityAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var value = await connection.ExecuteScalarAsync<decimal>(new CommandDefinition(
            """
            SELECT NVL(SUM(a.quantity), 0)
            FROM issue_allocations a
            JOIN requisition_lines rl ON rl.requisition_line_id = a.requisition_line_id
            WHERE rl.requisition_id = :id
            """,
            new { id = RequisitionId },
            cancellationToken: cancellationToken));
        return decimal.ToInt32(value);
    }

    private static async Task<long> ScalarAsync(
        OracleConnection connection, string sql, object parameters, CancellationToken cancellationToken)
    {
        var value = await connection.ExecuteScalarAsync<decimal?>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
        return value is null ? throw new InvalidOperationException($"查無資料：{sql}") : decimal.ToInt64(value.Value);
    }

    public async ValueTask DisposeAsync()
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        var p = new { suffix = _suffix, reqId = RequisitionId, deptId = DepartmentId };

        await connection.ExecuteAsync("""
            DELETE FROM issue_allocations
            WHERE requisition_line_id IN (
                SELECT requisition_line_id FROM requisition_lines WHERE requisition_id = :reqId)
            """, p);
        await connection.ExecuteAsync("DELETE FROM requisition_lines WHERE requisition_id = :reqId", p);
        await connection.ExecuteAsync("DELETE FROM requisitions WHERE requisition_id = :reqId", p);
        await connection.ExecuteAsync(
            "DELETE FROM stock_lots WHERE item_id IN (SELECT item_id FROM items WHERE item_code LIKE 'I' || :suffix || '%')", p);
        await connection.ExecuteAsync("DELETE FROM items WHERE item_code LIKE 'I' || :suffix || '%'", p);
        await connection.ExecuteAsync("DELETE FROM departments WHERE department_id = :deptId", p);
        await connection.ExecuteAsync("COMMIT");
    }
}
