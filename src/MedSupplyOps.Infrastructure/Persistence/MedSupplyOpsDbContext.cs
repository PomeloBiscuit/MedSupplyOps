using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MedSupplyOps.Domain.Inventory;
using MedSupplyOps.Domain.Requisitions;
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
    /// <summary>
    /// 尚無登入使用者的概念（目前尚不含 Controller / 認證）。
    /// 稽核欄位（CREATED_BY 等）是 NOT NULL，總得有值才寫得進去，
    /// 先以此常數佔位；真正的操作者之後經由注入的抽象取代。
    /// </summary>
    private static readonly string SystemActor = "system";

    public MedSupplyOpsDbContext(DbContextOptions<MedSupplyOpsDbContext> options)
        : base(options)
    {
    }

    public DbSet<Item> Items => Set<Item>();

    public DbSet<Department> Departments => Set<Department>();

    public DbSet<StockLot> StockLots => Set<StockLot>();

    public DbSet<Requisition> Requisitions => Set<Requisition>();

    public DbSet<RequisitionLine> RequisitionLines => Set<RequisitionLine>();

    public DbSet<IssueAllocation> IssueAllocations => Set<IssueAllocation>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampBookkeepingColumns();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampBookkeepingColumns();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MedSupplyOpsDbContext).Assembly);
    }

    /// <summary>
    /// 填三種「不屬於 Domain、但資料庫要求」的欄位：
    ///   1. 稽核欄位 CREATED_AT / CREATED_BY / UPDATED_AT / UPDATED_BY（NOT NULL，無預設）。
    ///   2. 請領明細的 LINE_NO —— 依聚合內 <c>_lines</c> 的順序給 1..n。
    ///      這是序號簿記，不是併發控制（FR-402 不在這裡處理）。
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
            }

            if (entry.State == EntityState.Added)
            {
                SetIfPresent(entry, "CreatedAt", now);
                SetIfPresent(entry, "CreatedBy", SystemActor);
            }
            else if (entry.State == EntityState.Modified)
            {
                SetIfPresent(entry, "UpdatedAt", now);
                SetIfPresent(entry, "UpdatedBy", SystemActor);
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
