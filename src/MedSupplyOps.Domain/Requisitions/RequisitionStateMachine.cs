namespace MedSupplyOps.Domain.Requisitions;

/// <summary>
/// 請領單狀態機（FR-304）。
///
/// 用「明列允許的轉換」而不是一堆 if/switch，理由是：
/// 明列之後，測試可以窮舉 (狀態 × 動作) 的完整笛卡兒積，斷言「恰好這幾組成立、其餘全部被拒」。
/// 若寫成 switch，測試只能抽樣 —— 而漏掉的那一格正好就是會出事的那一格。
///
/// 不合法的轉換一律回傳 false，呼叫端必須產生明確錯誤，不得靜默忽略（FR-304 明文要求）。
/// </summary>
public static class RequisitionStateMachine
{
    private static readonly Dictionary<(RequisitionStatus From, RequisitionAction Action), RequisitionStatus> AllowedTransitions =
        new()
        {
            [(RequisitionStatus.Draft, RequisitionAction.Submit)] = RequisitionStatus.PendingApproval,
            [(RequisitionStatus.PendingApproval, RequisitionAction.Approve)] = RequisitionStatus.Approved,
            [(RequisitionStatus.PendingApproval, RequisitionAction.Reject)] = RequisitionStatus.Rejected,
            [(RequisitionStatus.Approved, RequisitionAction.Issue)] = RequisitionStatus.Issued,
            [(RequisitionStatus.Issued, RequisitionAction.Close)] = RequisitionStatus.Closed,
        };

    /// <summary>允許的轉換總數。測試用它釘住「沒有人偷偷多開一條路」。</summary>
    public static int AllowedTransitionCount => AllowedTransitions.Count;

    public static bool TryTransition(RequisitionStatus from, RequisitionAction action, out RequisitionStatus to)
        => AllowedTransitions.TryGetValue((from, action), out to);
}
