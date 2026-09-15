using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace MedSupplyOps.Web.Models.Account;

public sealed record ProfileViewModel(
    string DisplayName,
    string Email,
    string RoleName,
    string DepartmentName,
    string Initial);

public sealed class ChangePasswordViewModel
{
    [Required(ErrorMessage = "請輸入目前密碼。")]
    [DataType(DataType.Password)]
    [Display(Name = "目前密碼")]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "請輸入新密碼。")]
    [DataType(DataType.Password)]
    [Display(Name = "新密碼")]
    public string NewPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "請再輸入一次新密碼。")]
    [DataType(DataType.Password)]
    [Display(Name = "確認新密碼")]
    [Compare(nameof(NewPassword), ErrorMessage = "兩次輸入的新密碼不一致。")]
    public string ConfirmPassword { get; set; } = string.Empty;

    public PasswordPolicyViewModel PasswordPolicy { get; set; } = PasswordPolicyViewModel.Empty;
}

public sealed record PasswordPolicyViewModel(
    int RequiredLength,
    int RequiredUniqueChars,
    bool RequireDigit,
    bool RequireLowercase,
    bool RequireUppercase,
    bool RequireNonAlphanumeric,
    IReadOnlyList<PasswordRuleViewModel> Rules)
{
    public static PasswordPolicyViewModel Empty { get; } = new(0, 0, false, false, false, false, []);

    public static PasswordPolicyViewModel From(PasswordOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var rules = new List<PasswordRuleViewModel>
        {
            new("length", $"新密碼必須至少 {options.RequiredLength} 個字元。"),
        };

        if (options.RequireUppercase)
        {
            rules.Add(new("uppercase", "新密碼必須至少包含一個大寫英文字母。"));
        }

        if (options.RequireLowercase)
        {
            rules.Add(new("lowercase", "新密碼必須至少包含一個小寫英文字母。"));
        }

        if (options.RequireDigit)
        {
            rules.Add(new("digit", "新密碼必須至少包含一個數字。"));
        }

        if (options.RequireNonAlphanumeric)
        {
            rules.Add(new("symbol", "新密碼必須至少包含一個符號。"));
        }

        if (options.RequiredUniqueChars > 1)
        {
            rules.Add(new("unique", $"新密碼必須至少包含 {options.RequiredUniqueChars} 個不同字元。"));
        }

        return new PasswordPolicyViewModel(
            options.RequiredLength,
            options.RequiredUniqueChars,
            options.RequireDigit,
            options.RequireLowercase,
            options.RequireUppercase,
            options.RequireNonAlphanumeric,
            rules);
    }
}

public sealed record PasswordRuleViewModel(string Code, string Description);
