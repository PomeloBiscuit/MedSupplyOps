using System;

namespace MedSupplyOps.Infrastructure.Persistence.Models;

/// <summary>
/// 發料配批結果（ISSUE_ALLOCATIONS）的持久化模型。
///
/// FK 欄位以純量屬性對映，不宣告導覽屬性：關聯完整性由資料庫的 FK 約束保證，
/// EF 這邊只負責把「哪筆明細、從哪一批、發多少、當下效期是多少」寫進去、讀得回來。
/// 少宣告一組導覽關聯 = 少一處會在對映層出錯的地方。
/// </summary>
public sealed class IssueAllocation
{
    public long Id { get; set; }

    public long RequisitionLineId { get; set; }

    public long StockLotId { get; set; }

    public int Quantity { get; set; }

    /// <summary>配批當下的效期快照。批次紀錄可能被更正，但當時發的效期不能被改寫。</summary>
    public DateOnly ExpiryDateAtIssue { get; set; }

    public DateTime IssuedAt { get; set; }

    public string IssuedBy { get; set; } = string.Empty;
}
