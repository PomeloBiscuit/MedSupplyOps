using System.Security.Claims;
using MedSupplyOps.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace MedSupplyOps.Web.Authorization;

/// <summary>
/// 三個角色與「沒有角色」的科室範圍規則，供 RequisitionsController 與 HomeController 共用。
///
/// ★ 這條規則原本只寫在 RequisitionsController 裡，且把「不是請領人」一律視為「不受限」。
/// 那在 RequisitionsController 是安全的，因為它的每個 Action 都要求三個角色之一，
/// 沒有角色的帳號進不了那個 Controller。但首頁只要求登入，原封不動搬過來的話，
/// 一個沒有任何角色的帳號會被判定「不受限」，看到全院資料。
/// 這裡把「沒有角色」獨立成第三種結果（<see cref="DepartmentScopeKind.NoScope"/>），
/// 對三個角色的行為維持完全不變。
/// </summary>
public sealed class DepartmentScopeResolver
{
    private readonly UserManager<ApplicationUser> _userManager;

    public DepartmentScopeResolver(UserManager<ApplicationUser> userManager)
    {
        _userManager = userManager;
    }

    public async Task<DepartmentScope> ResolveAsync(ClaimsPrincipal principal, string actor)
    {
        if (principal.IsInRole(ApplicationRoles.Requester))
        {
            var user = await _userManager.FindByNameAsync(actor);
            return DepartmentScope.RestrictedTo(user?.DepartmentId);
        }

        if (principal.IsInRole(ApplicationRoles.Storekeeper) || principal.IsInRole(ApplicationRoles.Administrator))
        {
            return DepartmentScope.Unrestricted;
        }

        return DepartmentScope.NoScope;
    }
}

public enum DepartmentScopeKind
{
    /// <summary>限自己的科室（科室為 null 代表帳號沒有配到科室，等同沒有範圍）。</summary>
    Restricted,

    /// <summary>不受限：看得到全院資料。</summary>
    Unrestricted,

    /// <summary>沒有任何角色：沒有範圍，不等於全院，呼叫端不應顯示任何業務資料。</summary>
    NoScope,
}

public readonly record struct DepartmentScope(DepartmentScopeKind Kind, long? DepartmentId)
{
    public static DepartmentScope Unrestricted => new(DepartmentScopeKind.Unrestricted, null);

    public static DepartmentScope NoScope => new(DepartmentScopeKind.NoScope, null);

    public static DepartmentScope RestrictedTo(long? departmentId) => new(DepartmentScopeKind.Restricted, departmentId);

    /// <summary>RequisitionsController 既有語意：限自己科室（含科室為 null 的情況）。</summary>
    public bool IsRestricted => Kind == DepartmentScopeKind.Restricted;
}
