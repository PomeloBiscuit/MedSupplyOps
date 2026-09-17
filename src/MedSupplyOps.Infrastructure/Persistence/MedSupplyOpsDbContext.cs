using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MedSupplyOps.Domain.Inventory;
using MedSupplyOps.Domain.Requisitions;
using MedSupplyOps.Infrastructure.Identity;
using MedSupplyOps.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;

namespace MedSupplyOps.Infrastructure.Persistence;

/// <summary>
/// 寫入路徑的 EF Core DbContext（讀取／報表路徑走 Dapper 手寫 SQL，見架構裁定）。
///
/// 對映一律寫在 <c>IEntityTypeConfiguration&lt;T&gt;</c>（Configurations 資料夾），
/// 不放在這裡，也不在 Domain 型別上加任何 attribute —— Domain 維持零套件依賴。
/// </summary>
public sealed class MedSupplyOpsDbContext : DbContext
{
    private readonly ICurrentUser _currentUser;

    /// <summary>
    /// ★ 設計裁定 D4：<paramref name="currentUser"/> 不可以是「取不到就補預設值」的實作。
    /// 取不到登入使用者時，<see cref="ICurrentUser.Actor"/> 必須丟例外——
    /// 這裡的建構子只負責接住那個抽象，不負責幫它兜一個安全的答案。
    /// </summary>
    public MedSupplyOpsDbContext(DbContextOptions<MedSupplyOpsDbContext> options, ICurrentUser currentUser)
        : base(options)
    {
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
    }

    public DbSet<Item> Items => Set<Item>();

    public DbSet<Department> Departments => Set<Department>();

    public DbSet<StorageLocation> StorageLocations => Set<StorageLocation>();

    public DbSet<StockLot> StockLots => Set<StockLot>();

    public DbSet<Requisition> Requisitions => Set<Requisition>();

    public DbSet<RequisitionLine> RequisitionLines => Set<RequisitionLine>();

    public DbSet<IssueAllocation> IssueAllocations => Set<IssueAllocation>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsureAuditLogsAreAppendOnly();
        StampBookkeepingColumns();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnsureAuditLogsAreAppendOnly();
        StampBookkeepingColumns();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ★ 不能用不篩選命名空間的 ApplyConfigurationsFromAssembly(assembly) ——
        //   那會連同 Identity.Configurations 底下的 Identity 表對映一起套用到「這個」模型。
        //   ApplyConfiguration<TEntity>() 只要被呼叫就會把 TEntity 加進目前的模型，
        //   而 Identity 實體（例如 IdentityUserLogin<string>）的複合鍵是在
        //   MedSupplyOpsIdentityDbContext（繼承 IdentityDbContext）的 OnModelCreating 基底
        //   邏輯裡設定的，這裡走不到那段基底邏輯，於是 EF 會抱怨那些實體沒有主鍵。
        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(MedSupplyOpsDbContext).Assembly,
            type => type.Namespace == typeof(Configurations.ItemConfiguration).Namespace);
    }

    /// <summary>
    /// 填三種「不屬於 Domain、但資料庫要求」的欄位：
    ///   1. 稽核欄位 CREATED_AT / CREATED_BY / UPDATED_AT / UPDATED_BY（NOT NULL，無預設）。
    ///   2. 請領明細的 LINE_NO —— 依聚合內 <c>_lines</c> 的順序給 1..n。
    ///      這是序號簿記，不是併發控制。
    ///   3. 請領單每次更新時遞增 ROW_VERSION，與 EF concurrency token 的舊值條件共同防止覆寫。
    /// 全部走 shadow / 純量屬性，Domain 型別不因 ORM 放寬封裝（設計裁定 D5）。
    /// </summary>
    private void StampBookkeepingColumns()
    {
        var now = DateTime.UtcNow;

        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is Requisition requisition)
            {
                AssignLineNumbers(requisition);

                if (entry.State == EntityState.Modified)
                {
                    var rowVersion = entry.Property("RowVersion");
                    rowVersion.CurrentValue = (long)rowVersion.OriginalValue! + 1;
                }
            }

            if (entry.State == EntityState.Added)
            {
                SetIfPresent(entry, "CreatedAt", now);
                if (entry.Metadata.FindProperty("CreatedBy") is not null)
                {
                    // ★ D4：_currentUser.Actor 取不到值時會丟例外——刻意讓它在這裡（寫入之前）
                    //   往外傳，而不是接住它、換成某個預設字串。整個 SaveChanges 因此失敗，
                    //   不會有任何一列寫進資料庫。見 ICurrentUser 的類別註解與 T3。
                    SetIfPresent(entry, "CreatedBy", _currentUser.Actor);
                }
            }
            else if (entry.State == EntityState.Modified)
            {
                SetIfPresent(entry, "UpdatedAt", now);
                if (entry.Metadata.FindProperty("UpdatedBy") is not null)
                {
                    SetIfPresent(entry, "UpdatedBy", _currentUser.Actor);
                }
            }
        }
    }

    private void AssignLineNumbers(Requisition requisition)
    {
        for (var i = 0; i < requisition.Lines.Count; i++)
        {
            var lineEntry = Entry(requisition.Lines[i]);
            if (lineEntry.State == EntityState.Added)
            {
                lineEntry.Property("LineNo").CurrentValue = i + 1;
            }
        }
    }

    private void EnsureAuditLogsAreAppendOnly()
    {
        if (ChangeTracker.Entries<AuditLog>().Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("AUDIT_LOGS 在應用程式端只允許新增，不允許修改或刪除。");
        }
    }

    private static void SetIfPresent(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry, string propertyName, object value)
    {
        // 只對「這個實體真的有這個欄位」時才動手：
        // Item / Department / StockLot / Requisition 有稽核欄位（部分是 shadow）；
        // IssueAllocation / AuditLog 沒有，這裡就自然略過。
        if (entry.Metadata.FindProperty(propertyName) is null)
        {
            return;
        }

        entry.Property(propertyName).CurrentValue = value;
    }
}
