using System.ComponentModel.DataAnnotations;
using MedSupplyOps.Domain.Requisitions;

namespace MedSupplyOps.Web.Models.Requisitions;

public sealed class RequisitionIndexViewModel
{
    public RequisitionStatus? Status { get; init; }

    public long? DepartmentId { get; init; }

    public DateOnly? CreatedFrom { get; init; }

    public DateOnly? CreatedTo { get; init; }

    public IReadOnlyList<RequisitionOptionViewModel> Departments { get; init; } = [];

    public IReadOnlyList<RequisitionListItemViewModel> Requisitions { get; init; } = [];
}

public sealed record RequisitionListItemViewModel(
    long Id,
    string RequisitionNo,
    string DepartmentName,
    RequisitionStatus Status,
    DateTime CreatedAt,
    int LineCount);

public sealed class CreateRequisitionViewModel
{
    [Range(1, long.MaxValue, ErrorMessage = "請選擇科室。")]
    [Display(Name = "科室")]
    public long DepartmentId { get; set; }

    public DateOnly AsOf { get; set; }

    [MinLength(1, ErrorMessage = "請領單至少需要一筆明細。")]
    public List<CreateRequisitionLineViewModel> Lines { get; set; } = [];

    public IReadOnlyList<RequisitionOptionViewModel> Departments { get; set; } = [];

    public IReadOnlyList<RequisitionItemOptionViewModel> Items { get; set; } = [];
}

public sealed class CreateRequisitionLineViewModel
{
    [Range(1, long.MaxValue, ErrorMessage = "請選擇品項。")]
    [Display(Name = "品項")]
    public long ItemId { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "請領數量必須為正整數。")]
    [Display(Name = "數量")]
    public int Quantity { get; set; }
}

public sealed record RequisitionOptionViewModel(long Id, string Name);

public sealed record RequisitionItemOptionViewModel(long Id, string Code, string Name, string UnitOfMeasure);

public sealed class RequisitionDetailsViewModel
{
    public long Id { get; init; }

    public string RequisitionNo { get; init; } = string.Empty;

    public string DepartmentName { get; init; } = string.Empty;

    public RequisitionStatus Status { get; init; }

    public string? RejectionReason { get; init; }

    public long RowVersion { get; init; }

    public DateTime CreatedAt { get; init; }

    public DateTime? SubmittedAt { get; init; }

    public DateTime? ApprovedAt { get; init; }

    public IReadOnlyList<RequisitionDetailsLineViewModel> Lines { get; init; } = [];

    public bool CanReview => Status == RequisitionStatus.PendingApproval;
}

public sealed record RequisitionDetailsLineViewModel(
    int LineNo,
    string ItemCode,
    string ItemName,
    string UnitOfMeasure,
    int Quantity);

public static class RequisitionStatusText
{
    public static string Get(RequisitionStatus status) => status switch
    {
        RequisitionStatus.Draft => "草稿",
        RequisitionStatus.PendingApproval => "待審核",
        RequisitionStatus.Approved => "已核准",
        RequisitionStatus.Rejected => "已駁回",
        RequisitionStatus.Issued => "已發料",
        RequisitionStatus.Closed => "已結案",
        _ => status.ToString(),
    };
}
