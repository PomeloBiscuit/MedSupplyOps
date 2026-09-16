using Microsoft.AspNetCore.Identity;

namespace MedSupplyOps.Infrastructure.Identity;

/// <summary>
/// 應用程式的 Identity 使用者。顯示姓名供畫面辨識；員工編號供院內身分與稽核軌跡使用。
/// </summary>
public sealed class ApplicationUser : IdentityUser
{
    public string DisplayName { get; set; } = string.Empty;

    public string? DisplayNameEn { get; set; }

    public string? EmployeeNo { get; set; }

    public long? DepartmentId { get; set; }
}
