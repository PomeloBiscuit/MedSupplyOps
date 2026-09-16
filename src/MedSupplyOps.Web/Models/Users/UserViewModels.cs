using System.ComponentModel.DataAnnotations;

namespace MedSupplyOps.Web.Models.Users;

public static class UserStatusFilters
{
    public const string All = "All";
    public const string Enabled = "Enabled";
    public const string Disabled = "Disabled";
}

public sealed class UserIndexViewModel
{
    public string? Role { get; init; }

    public string Status { get; init; } = UserStatusFilters.All;

    public IReadOnlyList<UserRoleOptionViewModel> Roles { get; init; } = [];

    public IReadOnlyList<UserListRowViewModel> Users { get; init; } = [];
}

public sealed record UserListRowViewModel(
    string Id,
    string DisplayName,
    string Email,
    string Role,
    string DepartmentName,
    bool IsEnabled,
    bool IsCurrentUser);

public sealed class CreateUserViewModel
{
    [Required(ErrorMessage = "請輸入 Email。")]
    [EmailAddress(ErrorMessage = "Email 格式不正確。")]
    [StringLength(256, ErrorMessage = "Email 不可超過 256 個字。")]
    [Display(Name = "Email")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "請輸入姓名。")]
    [StringLength(100, ErrorMessage = "姓名不可超過 100 個字。")]
    [Display(Name = "姓名")]
    public string DisplayName { get; set; } = string.Empty;

    [Required(ErrorMessage = "請選擇角色。")]
    [Display(Name = "角色")]
    public string Role { get; set; } = string.Empty;

    [Display(Name = "科室")]
    public long? DepartmentId { get; set; }

    [Required(ErrorMessage = "請輸入初始密碼。")]
    [DataType(DataType.Password)]
    [Display(Name = "初始密碼")]
    public string InitialPassword { get; set; } = string.Empty;

    public IReadOnlyList<UserRoleOptionViewModel> Roles { get; set; } = [];

    public IReadOnlyList<UserDepartmentOptionViewModel> Departments { get; set; } = [];
}

public sealed class EditUserViewModel
{
    public string Id { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "請輸入姓名。")]
    [StringLength(100, ErrorMessage = "姓名不可超過 100 個字。")]
    [Display(Name = "姓名")]
    public string DisplayName { get; set; } = string.Empty;

    [Required(ErrorMessage = "請選擇角色。")]
    [Display(Name = "角色")]
    public string Role { get; set; } = string.Empty;

    [Display(Name = "科室")]
    public long? DepartmentId { get; set; }

    public bool IsCurrentUser { get; set; }

    public IReadOnlyList<UserRoleOptionViewModel> Roles { get; set; } = [];

    public IReadOnlyList<UserDepartmentOptionViewModel> Departments { get; set; } = [];
}

public sealed record UserRoleOptionViewModel(string Value, string Label);

public sealed record UserDepartmentOptionViewModel(long Id, string Label);

public sealed record ResetPasswordResultViewModel(string DisplayName, string Email, string NewPassword);
