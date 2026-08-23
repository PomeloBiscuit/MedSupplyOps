namespace MedSupplyOps.Domain.Requisitions;

/// <summary>請領單（聚合根）。狀態轉換一律經過 <see cref="RequisitionStateMachine"/>。</summary>
public sealed class Requisition
{
    private readonly List<RequisitionLine> _lines = [];

    public Requisition(long id, long departmentId)
    {
        Id = id;
        DepartmentId = departmentId;
        Status = RequisitionStatus.Draft;
    }

    public long Id { get; }
    public long DepartmentId { get; }
    public RequisitionStatus Status { get; private set; }
    public string? RejectionReason { get; private set; }

    public IReadOnlyList<RequisitionLine> Lines => _lines;

    public void AddLine(RequisitionLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (Status != RequisitionStatus.Draft)
        {
            throw new InvalidOperationException($"只有草稿狀態可以編輯明細，目前狀態為 {Status}。");
        }

        _lines.Add(line);
    }

    public void Submit()
    {
        if (_lines.Count == 0)
        {
            throw new InvalidOperationException("沒有明細的請領單不可送出。");
        }

        Apply(RequisitionAction.Submit);
    }

    public void Approve() => Apply(RequisitionAction.Approve);

    public void Reject(string reason)
    {
        // 駁回必須有原因：這是流程要求，也是稽核軌跡的一部分（FR-302 / FR-403）。
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("駁回必須填寫原因。", nameof(reason));
        }

        Apply(RequisitionAction.Reject);
        RejectionReason = reason;
    }

    public void Issue() => Apply(RequisitionAction.Issue);

    public void Close() => Apply(RequisitionAction.Close);

    private void Apply(RequisitionAction action)
    {
        if (!RequisitionStateMachine.TryTransition(Status, action, out var next))
        {
            // 明確拒絕，不靜默忽略（FR-304）。
            throw new InvalidOperationException($"請領單 {Id} 在 {Status} 狀態下不允許執行 {action}。");
        }

        Status = next;
    }
}
