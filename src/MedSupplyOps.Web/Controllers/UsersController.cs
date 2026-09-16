using System.Data;
using System.Security.Cryptography;
using Dapper;
using MedSupplyOps.Infrastructure.Auditing;
using MedSupplyOps.Infrastructure.Identity;
using MedSupplyOps.Infrastructure.Persistence;
using MedSupplyOps.Web.Authorization;
using MedSupplyOps.Web.Models.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Localization;
using Oracle.ManagedDataAccess.Client;

namespace MedSupplyOps.Web.Controllers;

/// <summary>
/// 管理員專用的使用者生命週期入口。所有 Identity 變更與對應 audit_logs 寫入共用同一個
/// Oracle transaction；密碼只交給 UserManager，絕不讀寫 PasswordHash。
/// </summary>
[Authorize(Policy = AuthorizationPolicies.UserManage)]
public sealed class UsersController : Controller
{
    private const int AdministratorLockWaitSeconds = 5;
    private const int OraLockWaitTimeout = 30006;
    private const int OraResourceBusy = 54;
    private const int OraUniqueConstraint = 1;
    private const int GeneratedPasswordLength = 20;
    private const string ResetPasswordValueKey = "Users.ResetPassword.Value";
    private const string ResetPasswordNameKey = "Users.ResetPassword.Name";
    private const string ResetPasswordEmailKey = "Users.ResetPassword.Email";

    private static readonly DateTimeOffset AdministrativeLockoutEnd =
        new(9999, 12, 31, 0, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset AdministrativeLockoutThreshold =
        new(9999, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly string[] ManageableRoles =
    [
        ApplicationRoles.Requester,
        ApplicationRoles.Storekeeper,
        ApplicationRoles.Administrator,
    ];

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly MedSupplyOpsIdentityDbContext _identityDbContext;
    private readonly MedSupplyOpsDbContext _dbContext;
    private readonly ICurrentUser _currentUser;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public UsersController(
        UserManager<ApplicationUser> userManager,
        MedSupplyOpsIdentityDbContext identityDbContext,
        MedSupplyOpsDbContext dbContext,
        ICurrentUser currentUser,
        IStringLocalizer<SharedResource> localizer)
    {
        _userManager = userManager;
        _identityDbContext = identityDbContext;
        _dbContext = dbContext;
        _currentUser = currentUser;
        _localizer = localizer;
    }

    [HttpGet]
    public async Task<IActionResult> Index(string? role, string? status, CancellationToken cancellationToken)
    {
        var selectedRole = IsManageableRole(role) ? role : null;
        var selectedStatus = status is UserStatusFilters.Enabled or UserStatusFilters.Disabled
            ? status
            : UserStatusFilters.All;
        var currentUser = await _userManager.GetUserAsync(User);

        var users = await _identityDbContext.Users.AsNoTracking()
            .OrderBy(user => user.DisplayName)
            .ThenBy(user => user.Email)
            .ToListAsync(cancellationToken);
        var roleRows = await (
            from userRole in _identityDbContext.UserRoles.AsNoTracking()
            join identityRole in _identityDbContext.Roles.AsNoTracking() on userRole.RoleId equals identityRole.Id
            select new { userRole.UserId, identityRole.Name })
            .ToListAsync(cancellationToken);
        var rolesByUser = roleRows
            .GroupBy(row => row.UserId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(row => row.Name ?? string.Empty)
                    .Where(IsManageableRole)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);
        var departments = await _dbContext.Departments.AsNoTracking()
            .Where(department => !department.IsDeleted)
            .ToDictionaryAsync(department => department.Id, department => department.Name, cancellationToken);

        var rows = users.Select(user =>
        {
            var userRoles = rolesByUser.GetValueOrDefault(user.Id, []);
            var isEnabled = !IsAdministrativelyDisabled(user);
            var departmentName = user.DepartmentId is long departmentId && departments.TryGetValue(departmentId, out var name)
                ? name
                : _localizer["未指定"].Value;
            return new
            {
                Roles = userRoles,
                Row = new UserListRowViewModel(
                    user.Id,
                    DisplayName(user),
                    user.Email ?? user.UserName ?? string.Empty,
                    user.EmployeeNo,
                    DisplayRoles(userRoles),
                    departmentName,
                    isEnabled,
                    string.Equals(user.Id, currentUser?.Id, StringComparison.Ordinal)),
            };
        });

        if (selectedRole is not null)
        {
            rows = rows.Where(item => item.Roles.Contains(selectedRole, StringComparer.Ordinal));
        }

        rows = selectedStatus switch
        {
            UserStatusFilters.Enabled => rows.Where(item => item.Row.IsEnabled),
            UserStatusFilters.Disabled => rows.Where(item => !item.Row.IsEnabled),
            _ => rows,
        };

        return View(new UserIndexViewModel
        {
            Role = selectedRole,
            Status = selectedStatus,
            Roles = RoleOptions(),
            Users = rows.Select(item => item.Row).ToList(),
        });
    }

    [HttpGet]
    public async Task<IActionResult> Create(CancellationToken cancellationToken)
    {
        var model = new CreateUserViewModel();
        await PopulateOptionsAsync(model, cancellationToken);
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateUserViewModel model, CancellationToken cancellationToken)
    {
        Normalize(model);
        RevalidateNormalizedFields(
            model,
            nameof(model.Email),
            nameof(model.DisplayName),
            nameof(model.EmployeeNo),
            nameof(model.Role));
        await ValidateEmployeeNoUniqueAsync(model.EmployeeNo, excludedUserId: null, cancellationToken);
        await ValidateRoleAndDepartmentAsync(model.Role, model.DepartmentId, nameof(model.DepartmentId), cancellationToken);
        if (!ModelState.IsValid)
        {
            await PopulateOptionsAsync(model, cancellationToken);
            return View(model);
        }

        var user = new ApplicationUser
        {
            UserName = model.Email,
            Email = model.Email,
            EmailConfirmed = true,
            DisplayName = model.DisplayName,
            EmployeeNo = model.EmployeeNo,
            DepartmentId = model.Role == ApplicationRoles.Requester ? model.DepartmentId : null,
            LockoutEnabled = true,
        };

        await using var transaction = await _identityDbContext.Database.BeginTransactionAsync(cancellationToken);
        IdentityResult createResult;
        try
        {
            createResult = await _userManager.CreateAsync(user, model.InitialPassword);
        }
        catch (Exception exception) when (IsEmployeeNoUniqueConstraintViolation(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            ModelState.AddModelError(nameof(model.EmployeeNo), _localizer["員工編號重複。"]);
            await PopulateOptionsAsync(model, cancellationToken);
            return View(model);
        }

        if (!createResult.Succeeded)
        {
            await transaction.RollbackAsync(cancellationToken);
            AddIdentityErrors(createResult);
            await PopulateOptionsAsync(model, cancellationToken);
            return View(model);
        }

        var roleResult = await _userManager.AddToRoleAsync(user, model.Role);
        if (!roleResult.Succeeded)
        {
            await transaction.RollbackAsync(cancellationToken);
            AddIdentityErrors(roleResult);
            await PopulateOptionsAsync(model, cancellationToken);
            return View(model);
        }

        await InsertAuditAsync(
            user.Id,
            AuditValues.CreateAction,
            oldValue: null,
            UserAuditJson(model.Role, user.DepartmentId, user.EmployeeNo, enabled: true),
            transaction,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        TempData["SuccessMessage"] = _localizer["使用者 {0} 已新增。", user.Email ?? user.Id].Value;
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(string id, CancellationToken cancellationToken)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        var currentUser = await _userManager.GetUserAsync(User);
        var roles = await _userManager.GetRolesAsync(user);
        var model = new EditUserViewModel
        {
            Id = user.Id,
            Email = user.Email ?? user.UserName ?? string.Empty,
            DisplayName = user.DisplayName,
            EmployeeNo = user.EmployeeNo,
            Role = roles.FirstOrDefault(IsManageableRole) ?? string.Empty,
            DepartmentId = user.DepartmentId,
            IsCurrentUser = string.Equals(user.Id, currentUser?.Id, StringComparison.Ordinal),
        };
        await PopulateOptionsAsync(model, cancellationToken);
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(
        string id,
        [Bind("DisplayName,EmployeeNo,Role,DepartmentId")] EditUserViewModel model,
        CancellationToken cancellationToken)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        var currentUser = await _userManager.GetUserAsync(User);
        if (currentUser is null)
        {
            return Challenge();
        }

        model.Id = user.Id;
        model.Email = user.Email ?? user.UserName ?? string.Empty;
        model.IsCurrentUser = string.Equals(user.Id, currentUser.Id, StringComparison.Ordinal);
        Normalize(model);
        RevalidateNormalizedFields(model, nameof(model.DisplayName), nameof(model.EmployeeNo), nameof(model.Role));
        await ValidateEmployeeNoUniqueAsync(model.EmployeeNo, user.Id, cancellationToken);
        await ValidateRoleAndDepartmentAsync(model.Role, model.DepartmentId, nameof(model.DepartmentId), cancellationToken);
        if (model.IsCurrentUser && model.Role != ApplicationRoles.Administrator)
        {
            ModelState.AddModelError(nameof(model.Role), _localizer["管理員不能把自己的角色降級，請由另一位管理員操作。"]);
        }

        if (!ModelState.IsValid)
        {
            await PopulateOptionsAsync(model, cancellationToken);
            return View(model);
        }

        await using var transaction = await _identityDbContext.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            await LockAdministratorRoleAsync(transaction, cancellationToken);
            var oldRoles = (await _userManager.GetRolesAsync(user)).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            var oldRole = oldRoles.FirstOrDefault(IsManageableRole) ?? string.Empty;
            var oldDepartmentId = user.DepartmentId;
            var oldEmployeeNo = user.EmployeeNo;
            var wasEnabledAdministrator = oldRoles.Contains(ApplicationRoles.Administrator, StringComparer.Ordinal)
                && !IsAdministrativelyDisabled(user);
            if (wasEnabledAdministrator && model.Role != ApplicationRoles.Administrator
                && await CountEnabledAdministratorsAsync(transaction, cancellationToken) <= 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                ModelState.AddModelError(nameof(model.Role), _localizer["系統必須至少保留一位啟用中的管理員。"]);
                await PopulateOptionsAsync(model, cancellationToken);
                return View(model);
            }

            user.DisplayName = model.DisplayName;
            user.EmployeeNo = model.EmployeeNo;
            user.DepartmentId = model.Role == ApplicationRoles.Requester ? model.DepartmentId : null;
            EnsureSucceeded(await _userManager.UpdateAsync(user), _localizer["更新使用者資料失敗。"]);

            var roleChanged = oldRoles.Length != 1 || !oldRoles.Contains(model.Role, StringComparer.Ordinal);
            if (roleChanged)
            {
                if (oldRoles.Length > 0)
                {
                    EnsureSucceeded(await _userManager.RemoveFromRolesAsync(user, oldRoles), _localizer["移除舊角色失敗。"]);
                }

                EnsureSucceeded(await _userManager.AddToRoleAsync(user, model.Role), _localizer["指派新角色失敗。"]);
                EnsureSucceeded(await _userManager.UpdateSecurityStampAsync(user), _localizer["讓既有登入失效時發生錯誤。"]);
            }

            await InsertAuditAsync(
                user.Id,
                AuditValues.UpdateAction,
                UserAuditJson(oldRole, oldDepartmentId, oldEmployeeNo, !IsAdministrativelyDisabled(user)),
                UserAuditJson(model.Role, user.DepartmentId, user.EmployeeNo, !IsAdministrativelyDisabled(user)),
                transaction,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception) when (IsEmployeeNoUniqueConstraintViolation(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            ModelState.AddModelError(nameof(model.EmployeeNo), _localizer["員工編號重複。"]);
            await PopulateOptionsAsync(model, cancellationToken);
            return View(model);
        }
        catch (OracleException exception) when (IsAdministratorLockTimeout(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            ModelState.AddModelError(string.Empty, _localizer["另一位管理員正在異動使用者，請稍後再試。"]);
            await PopulateOptionsAsync(model, cancellationToken);
            return View(model);
        }

        TempData["SuccessMessage"] = _localizer["使用者 {0} 已更新。", user.Email ?? user.Id].Value;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetEnabled(string id, bool enabled, CancellationToken cancellationToken)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        var currentUser = await _userManager.GetUserAsync(User);
        if (currentUser is null)
        {
            return Challenge();
        }

        var currentlyEnabled = !IsAdministrativelyDisabled(user);
        if (currentlyEnabled == enabled)
        {
            return RedirectToAction(nameof(Index));
        }

        if (!enabled && string.Equals(user.Id, currentUser.Id, StringComparison.Ordinal))
        {
            TempData["ErrorMessage"] = _localizer["管理員不能停用自己的帳號。"].Value;
            return RedirectToAction(nameof(Index));
        }

        await using var transaction = await _identityDbContext.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            await LockAdministratorRoleAsync(transaction, cancellationToken);
            var roles = (await _userManager.GetRolesAsync(user)).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            var role = roles.FirstOrDefault(IsManageableRole) ?? string.Empty;
            if (!enabled && roles.Contains(ApplicationRoles.Administrator, StringComparer.Ordinal)
                && await CountEnabledAdministratorsAsync(transaction, cancellationToken) <= 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                TempData["ErrorMessage"] = _localizer["系統必須至少保留一位啟用中的管理員。"].Value;
                return RedirectToAction(nameof(Index));
            }

            EnsureSucceeded(await _userManager.SetLockoutEnabledAsync(user, true), _localizer["更新帳號狀態失敗。"]);
            EnsureSucceeded(
                await _userManager.SetLockoutEndDateAsync(user, enabled ? null : AdministrativeLockoutEnd),
                _localizer["更新帳號狀態失敗。"]);
            if (enabled)
            {
                EnsureSucceeded(await _userManager.ResetAccessFailedCountAsync(user), _localizer["清除登入失敗次數時發生錯誤。"]);
            }

            EnsureSucceeded(await _userManager.UpdateSecurityStampAsync(user), _localizer["讓既有登入失效時發生錯誤。"]);
            await InsertAuditAsync(
                user.Id,
                enabled ? AuditValues.EnableAction : AuditValues.DisableAction,
                UserAuditJson(role, user.DepartmentId, user.EmployeeNo, currentlyEnabled),
                UserAuditJson(role, user.DepartmentId, user.EmployeeNo, enabled),
                transaction,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (OracleException exception) when (IsAdministratorLockTimeout(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["ErrorMessage"] = _localizer["另一位管理員正在異動使用者，請稍後再試。"].Value;
            return RedirectToAction(nameof(Index));
        }

        TempData["SuccessMessage"] = enabled
            ? _localizer["使用者 {0} 已啟用。", user.Email ?? user.Id].Value
            : _localizer["使用者 {0} 已停用。", user.Email ?? user.Id].Value;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(string id, CancellationToken cancellationToken)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        await using var transaction = await _identityDbContext.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            await LockAdministratorRoleAsync(transaction, cancellationToken);
            if (await CountEnabledAdministratorsAsync(transaction, cancellationToken) < 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                TempData["ErrorMessage"] = _localizer["系統目前沒有啟用中的管理員，無法重設密碼。"].Value;
                return RedirectToAction(nameof(Index));
            }

            var roles = (await _userManager.GetRolesAsync(user)).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            var role = roles.FirstOrDefault(IsManageableRole) ?? string.Empty;
            var newPassword = GeneratePassword();
            var resetToken = await _userManager.GeneratePasswordResetTokenAsync(user);
            EnsureSucceeded(await _userManager.ResetPasswordAsync(user, resetToken, newPassword), _localizer["重設密碼失敗。"]);
            EnsureSucceeded(await _userManager.UpdateSecurityStampAsync(user), _localizer["讓既有登入失效時發生錯誤。"]);

            var auditValue = UserAuditJson(role, user.DepartmentId, user.EmployeeNo, !IsAdministrativelyDisabled(user));
            await InsertAuditAsync(
                user.Id,
                AuditValues.ResetPasswordAction,
                auditValue,
                auditValue,
                transaction,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            // ★ 明文只放在一次性 TempData。不得寫入 audit、log、資料庫或其他持久狀態。
            TempData[ResetPasswordValueKey] = newPassword;
            TempData[ResetPasswordNameKey] = DisplayName(user);
            TempData[ResetPasswordEmailKey] = user.Email ?? user.UserName ?? string.Empty;
        }
        catch (OracleException exception) when (IsAdministratorLockTimeout(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["ErrorMessage"] = _localizer["另一位管理員正在異動使用者，請稍後再試。"].Value;
            return RedirectToAction(nameof(Index));
        }

        return RedirectToAction(nameof(ResetPasswordResult));
    }

    [HttpGet]
    public IActionResult ResetPasswordResult()
    {
        if (TempData[ResetPasswordValueKey] is not string password
            || TempData[ResetPasswordNameKey] is not string displayName
            || TempData[ResetPasswordEmailKey] is not string email)
        {
            TempData["ErrorMessage"] = _localizer["新密碼已顯示過或已失效，請重新執行重設密碼。"].Value;
            return RedirectToAction(nameof(Index));
        }

        return View(new ResetPasswordResultViewModel(displayName, email, password));
    }

    private async Task ValidateRoleAndDepartmentAsync(
        string role,
        long? departmentId,
        string departmentField,
        CancellationToken cancellationToken)
    {
        if (!IsManageableRole(role))
        {
            ModelState.AddModelError(nameof(CreateUserViewModel.Role), _localizer["選擇的角色無效。"]);
            return;
        }

        if (role != ApplicationRoles.Requester)
        {
            return;
        }

        if (departmentId is null)
        {
            ModelState.AddModelError(departmentField, _localizer["請領人必須選擇科室。"]);
            return;
        }

        var exists = await _dbContext.Departments.AsNoTracking().AnyAsync(
            department => department.Id == departmentId && department.IsActive && !department.IsDeleted,
            cancellationToken);
        if (!exists)
        {
            ModelState.AddModelError(departmentField, _localizer["選擇的科室不存在或已停用。"]);
        }
    }

    private async Task ValidateEmployeeNoUniqueAsync(
        string? employeeNo,
        string? excludedUserId,
        CancellationToken cancellationToken)
    {
        if (employeeNo is null)
        {
            return;
        }

        var usersWithEmployeeNo = _identityDbContext.Users.AsNoTracking()
            .Where(user => user.EmployeeNo == employeeNo);
        if (excludedUserId is not null)
        {
            usersWithEmployeeNo = usersWithEmployeeNo.Where(user => user.Id != excludedUserId);
        }

        var duplicate = await usersWithEmployeeNo.AnyAsync(cancellationToken);
        if (duplicate)
        {
            ModelState.AddModelError(nameof(CreateUserViewModel.EmployeeNo), _localizer["員工編號重複。"]);
        }
    }

    private async Task PopulateOptionsAsync(CreateUserViewModel model, CancellationToken cancellationToken)
    {
        model.Roles = RoleOptions();
        model.Departments = await DepartmentOptionsAsync(cancellationToken);
    }

    private async Task PopulateOptionsAsync(EditUserViewModel model, CancellationToken cancellationToken)
    {
        model.Roles = RoleOptions();
        model.Departments = await DepartmentOptionsAsync(cancellationToken);
    }

    private IReadOnlyList<UserRoleOptionViewModel> RoleOptions() =>
    [
        new(ApplicationRoles.Requester, _localizer["請領人"]),
        new(ApplicationRoles.Storekeeper, _localizer["庫管員"]),
        new(ApplicationRoles.Administrator, _localizer["管理員"]),
    ];

    private async Task<IReadOnlyList<UserDepartmentOptionViewModel>> DepartmentOptionsAsync(CancellationToken cancellationToken)
        => await _dbContext.Departments.AsNoTracking()
            .Where(department => department.IsActive && !department.IsDeleted)
            .OrderBy(department => department.Code)
            .Select(department => new UserDepartmentOptionViewModel(
                department.Id,
                department.Code + " — " + department.Name))
            .ToListAsync(cancellationToken);

    private async Task LockAdministratorRoleAsync(
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        var roleId = await _identityDbContext.Database.GetDbConnection().QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            $"SELECT id FROM identity_roles WHERE normalized_name = :normalizedName FOR UPDATE WAIT {AdministratorLockWaitSeconds}",
            new { normalizedName = ApplicationRoles.Administrator.ToUpperInvariant() },
            transaction.GetDbTransaction(),
            cancellationToken: cancellationToken));
        if (string.IsNullOrWhiteSpace(roleId))
        {
            throw new InvalidOperationException("找不到 Administrator 角色，無法安全異動使用者。");
        }
    }

    private async Task<int> CountEnabledAdministratorsAsync(
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
        => await _identityDbContext.Database.GetDbConnection().ExecuteScalarAsync<int>(new CommandDefinition(
            """
            SELECT COUNT(*)
            FROM identity_users u
            JOIN identity_user_roles ur ON ur.user_id = u.id
            JOIN identity_roles r ON r.id = ur.role_id
            WHERE r.normalized_name = :normalizedRole
              AND (u.lockout_enabled = 0 OR u.lockout_end IS NULL OR u.lockout_end < :disabledThreshold)
            """,
            new
            {
                normalizedRole = ApplicationRoles.Administrator.ToUpperInvariant(),
                disabledThreshold = AdministrativeLockoutThreshold,
            },
            transaction.GetDbTransaction(),
            cancellationToken: cancellationToken));

    private async Task InsertAuditAsync(
        string userId,
        string action,
        string? oldValue,
        string? newValue,
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
        => await _identityDbContext.Database.GetDbConnection().ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO audit_logs (entity_type, entity_id, action, actor, occurred_at, old_value, new_value)
            VALUES (:entityType, :entityId, :action, :actor, :occurredAt, :oldValue, :newValue)
            """,
            new
            {
                entityType = AuditValues.UserEntity,
                entityId = userId,
                action,
                actor = _currentUser.Actor,
                occurredAt = DateTime.UtcNow,
                oldValue,
                newValue,
            },
            transaction.GetDbTransaction(),
            cancellationToken: cancellationToken));

    private static string UserAuditJson(string role, long? departmentId, string? employeeNo, bool enabled)
        => AuditValues.ToJson(new { Role = role, DepartmentId = departmentId, EmployeeNo = employeeNo, Enabled = enabled });

    private string DisplayRoles(IEnumerable<string> roles)
    {
        var labels = roles.Select(role => role switch
        {
            ApplicationRoles.Requester => _localizer["請領人"].Value,
            ApplicationRoles.Storekeeper => _localizer["庫管員"].Value,
            ApplicationRoles.Administrator => _localizer["管理員"].Value,
            _ => role,
        }).ToList();
        return labels.Count == 0 ? _localizer["未指派角色"] : string.Join(_localizer["清單分隔符"], labels);
    }

    private static string DisplayName(ApplicationUser user)
        => string.IsNullOrWhiteSpace(user.DisplayName)
            ? user.Email ?? user.UserName ?? user.Id
            : user.DisplayName;

    private static bool IsManageableRole(string? role)
        => role is not null && ManageableRoles.Contains(role, StringComparer.Ordinal);

    private static bool IsAdministrativelyDisabled(ApplicationUser user)
        => user.LockoutEnabled && user.LockoutEnd >= AdministrativeLockoutThreshold;

    private void RevalidateNormalizedFields(object model, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            ModelState.Remove(propertyName);
        }

        TryValidateModel(model);
    }

    private static void Normalize(CreateUserViewModel model)
    {
        model.Email = (model.Email ?? string.Empty).Trim();
        model.DisplayName = (model.DisplayName ?? string.Empty).Trim();
        model.EmployeeNo = NormalizeEmployeeNo(model.EmployeeNo);
        model.Role = (model.Role ?? string.Empty).Trim();
    }

    private static void Normalize(EditUserViewModel model)
    {
        model.DisplayName = (model.DisplayName ?? string.Empty).Trim();
        model.EmployeeNo = NormalizeEmployeeNo(model.EmployeeNo);
        model.Role = (model.Role ?? string.Empty).Trim();
    }

    private static string? NormalizeEmployeeNo(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    private void AddIdentityErrors(IdentityResult result)
    {
        foreach (var error in result.Errors)
        {
            ModelState.AddModelError(string.Empty, error.Description);
        }
    }

    private static void EnsureSucceeded(IdentityResult result, LocalizedString message)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"{message.Value} {string.Join("；", result.Errors.Select(error => error.Description))}");
        }
    }

    private static bool IsAdministratorLockTimeout(OracleException exception)
        => exception.Number is OraLockWaitTimeout or OraResourceBusy;

    private static bool IsEmployeeNoUniqueConstraintViolation(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is OracleException oracleException
                && oracleException.Number == OraUniqueConstraint
                && oracleException.Message.Contains("UX_IDENTITY_USERS_EMPLOYEE_NO", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string GeneratePassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!@#$%*-_+=";
        const string all = upper + lower + digits + symbols;
        var characters = new char[GeneratedPasswordLength];
        characters[0] = RandomCharacter(upper);
        characters[1] = RandomCharacter(lower);
        characters[2] = RandomCharacter(digits);
        characters[3] = RandomCharacter(symbols);
        for (var index = 4; index < characters.Length; index++)
        {
            characters[index] = RandomCharacter(all);
        }

        for (var index = characters.Length - 1; index > 0; index--)
        {
            var swapIndex = RandomNumberGenerator.GetInt32(index + 1);
            (characters[index], characters[swapIndex]) = (characters[swapIndex], characters[index]);
        }

        return new string(characters);
    }

    private static char RandomCharacter(string characters)
        => characters[RandomNumberGenerator.GetInt32(characters.Length)];
}
