using MedSupplyOps.Domain.Requisitions;
using MedSupplyOps.Infrastructure.Auditing;
using MedSupplyOps.Infrastructure.Queries;

namespace MedSupplyOps.Web.Models.Home;

/// <summary>庫管員／管理員（不受限）看到的營運儀表板。</summary>
public sealed class OperationsDashboardViewModel
{
    public bool CanCreateRequisition { get; init; }

    public int PendingApprovalCount { get; init; }

    public int ApprovedAwaitingIssueCount { get; init; }

    public int ExpiringWithin30DaysCount { get; init; }

    public int BelowSafetyStockCount { get; init; }

    public int ExpiredInStockCount { get; init; }

    public IReadOnlyList<ApprovedIssueQueueItemViewModel> ApprovedIssueQueue { get; init; } = [];

    public IReadOnlyList<AuditFeedItemViewModel> RecentAudit { get; init; } = [];
}

public sealed record ApprovedIssueQueueItemViewModel(
    long Id,
    string RequisitionNo,
    string DepartmentName,
    int LineCount,
    DateTime ApprovedAtUtc);

/// <summary>請領人（限自己科室）看到的儀表板。</summary>
public sealed class RequesterDashboardViewModel
{
    public bool CanCreateRequisition { get; init; }

    public int PendingApprovalCount { get; init; }

    public int ApprovedAwaitingIssueCount { get; init; }

    public int IssuedThisMonthCount { get; init; }

    public IReadOnlyList<RequesterRequisitionItemViewModel> RecentRequisitions { get; init; } = [];
}

public sealed record RequesterRequisitionItemViewModel(
    long Id,
    string RequisitionNo,
    DateTime CreatedAt,
    int LineCount,
    RequisitionStatus Status,
    string? RejectionReason);

/// <summary>首頁「最近異動」列表的單一列，已轉成畫面用文字。</summary>
public sealed record AuditFeedItemViewModel(DateTime OccurredAtUtc, string ActorDisplay, string ActionText, string TargetText, long? TargetRequisitionId)
{
    public static AuditFeedItemViewModel FromEntry(AuditFeedEntry entry) => new(
        entry.OccurredAt,
        entry.DisplayName ?? entry.Actor,
        ActionTextOf(entry.Action),
        TargetTextOf(entry),
        entry.EntityType == AuditValues.RequisitionEntity && long.TryParse(entry.EntityId, out var requisitionId)
            ? requisitionId
            : null);

    private static string ActionTextOf(string action) => action switch
    {
        "Create" => "建立",
        "Approve" => "核准",
        "Reject" => "駁回",
        "Issue" => "發料",
        "Update" => "編輯",
        "Delete" => "停用",
        "Receive" => "入庫",
        _ => action,
    };

    // ★ 不認得的 entity_type／缺漏的對映資料一律回退成「{entity_type} #{entity_id}」，
    //   不可以隱藏這筆紀錄，也不可以丟例外——之後任何人新增一種稽核類型，首頁都不能因此壞掉。
    private static string TargetTextOf(AuditFeedEntry entry) => entry.EntityType switch
    {
        AuditValues.RequisitionEntity => entry.RequisitionNo is { } no ? $"請領單 {no}" : FallbackTarget(entry),
        AuditValues.ItemEntity => entry.ItemCode ?? FallbackTarget(entry),
        AuditValues.StockLotEntity => entry.LotNumber ?? FallbackTarget(entry),
        _ => FallbackTarget(entry),
    };

    private static string FallbackTarget(AuditFeedEntry entry) => $"{entry.EntityType} #{entry.EntityId}";
}
