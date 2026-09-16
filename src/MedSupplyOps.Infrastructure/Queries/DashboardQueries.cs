using System.Data.Common;
using Dapper;

namespace MedSupplyOps.Infrastructure.Queries;

/// <summary>首頁工作儀表板專用的讀取路徑。同樣以 Dapper 執行明確的 Oracle SQL。</summary>
public sealed class DashboardQueries
{
    private readonly DbConnection _connection;

    public DashboardQueries(DbConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    /// <summary>取得未過期判定成立、仍有數量、品項未停用的過期批次數（「已過期仍在庫」警示卡）。</summary>
    public async Task<int> GetExpiredInStockLotCountAsync(
        DateOnly asOf,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT COUNT(*)
            FROM stock_lots l
            INNER JOIN items i ON i.item_id = l.item_id
            WHERE i.is_deleted = 0
              AND l.quantity > 0
              AND l.expiry_date < :asOf
            """;

        await EnsureOpenAsync(cancellationToken);
        var asOfDate = asOf.ToDateTime(TimeOnly.MinValue);
        var command = new CommandDefinition(sql, new { asOf = asOfDate }, transaction, cancellationToken: cancellationToken);
        return await _connection.ExecuteScalarAsync<int>(command);
    }

    /// <summary>
    /// 最新 N 筆稽核紀錄，含畫面需要的顯示姓名與各種實體的識別文字。
    /// 排序 occurred_at DESC, audit_log_id DESC —— 第二鍵是必須的，
    /// 否則同一秒的多筆紀錄順序不固定。
    /// 不認得的 entity_type（RequisitionCode／ItemCode／LotNumber 皆為 null）
    /// 由呼叫端決定怎麼顯示，這裡只負責把能查到的資料帶回去，不可以因為查不到就漏掉整筆。
    /// </summary>
    public async Task<IReadOnlyList<AuditFeedEntry>> GetRecentAuditEntriesAsync(
        int count,
        CancellationToken cancellationToken = default)
    {
        // ★ entity_id 是字串欄位，JOIN 條件裡的轉換必須是 DEFAULT NULL ON CONVERSION ERROR。
        //   Oracle 不保證先比對 entity_type 再轉換：原本的 TO_NUMBER(a.entity_id) 遇到一筆非數字 id
        //   （例如將來以 GUID 為鍵的稽核類型），整個營運儀表板就是 HTTP 500。見 L-029。
        const string sql = """
            SELECT a.entity_type AS EntityType,
                   a.entity_id AS EntityId,
                   a.action AS Action,
                   a.actor AS Actor,
                   a.occurred_at AS OccurredAt,
                   u.display_name AS DisplayName,
                   u.display_name_en AS DisplayNameEn,
                   r.requisition_no AS RequisitionNo,
                   i.item_code AS ItemCode,
                   l.lot_number AS LotNumber
            FROM audit_logs a
            LEFT JOIN identity_users u ON u.user_name = a.actor
            LEFT JOIN requisitions r ON a.entity_type = 'Requisition' AND r.requisition_id = TO_NUMBER(a.entity_id DEFAULT NULL ON CONVERSION ERROR)
            LEFT JOIN items i ON a.entity_type = 'Item' AND i.item_id = TO_NUMBER(a.entity_id DEFAULT NULL ON CONVERSION ERROR)
            LEFT JOIN stock_lots l ON a.entity_type = 'StockLot' AND l.stock_lot_id = TO_NUMBER(a.entity_id DEFAULT NULL ON CONVERSION ERROR)
            ORDER BY a.occurred_at DESC, a.audit_log_id DESC
            FETCH FIRST :count ROWS ONLY
            """;

        await EnsureOpenAsync(cancellationToken);
        var command = new CommandDefinition(sql, new { count }, cancellationToken: cancellationToken);
        return (await _connection.QueryAsync<AuditFeedEntry>(command)).AsList();
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

/// <summary>首頁「最近異動」單筆稽核紀錄。RequisitionNo／ItemCode／LotNumber 依 EntityType 至多一個非 null。</summary>
public sealed record AuditFeedEntry(
    string EntityType,
    string EntityId,
    string Action,
    string Actor,
    DateTime OccurredAt,
    string? DisplayName,
    string? DisplayNameEn,
    string? RequisitionNo,
    string? ItemCode,
    string? LotNumber);
