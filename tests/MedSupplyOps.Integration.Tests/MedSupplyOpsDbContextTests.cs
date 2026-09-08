using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MedSupplyOps.Domain.Inventory;
using MedSupplyOps.Domain.Requisitions;
using MedSupplyOps.Infrastructure.Persistence;
using MedSupplyOps.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace MedSupplyOps.Integration.Tests;

/// <summary>
/// 對真的 Oracle（docker compose 的 medsupplyops-oracle 容器）跑的對映測試。
///
/// 這裡不做「連得上就算過」——每一條都寫入具體欄位、清掉追蹤、再從資料庫讀回來逐欄比對。
/// 只要有一個欄位對映到錯的資料庫欄位名，讀回來的值就會不符（或查詢直接 ORA 錯），測試變紅。
/// （見 T3 的 <c>WrongColumnNameMapping_makes_a_roundtrip_test_fail</c> 對照。）
///
/// 每條測試在自己的交易裡跑、結束一律 rollback（設計裁定 D6）：
/// 不留任何資料，連續跑幾次結果都一樣。
/// </summary>
public sealed class MedSupplyOpsDbContextTests
{
    private readonly ITestOutputHelper _output;

    public MedSupplyOpsDbContextTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..12];

    [Fact]
    public async Task Item_round_trips_every_mapped_column()
    {
        await using var ctx = OracleTestDatabase.CreateContext();
        await using var tx = await ctx.Database.BeginTransactionAsync();

        var code = "IT-" + Suffix();
        var item = new Item
        {
            Code = code,
            Name = "中心靜脈導管組",
            Specification = "5Fr x 20cm 雙腔",
            UnitOfMeasure = "組",
            TracksLot = true,
            TracksExpiry = false,
            SafetyStockQty = 7,
        };
        ctx.Items.Add(item);
        await ctx.SaveChangesAsync();

        Assert.True(item.Id > 0); // ITEM_ID 是 GENERATED ALWAYS AS IDENTITY，由資料庫產生並回填

        ctx.ChangeTracker.Clear();
        var read = await ctx.Items.AsNoTracking().SingleAsync(x => x.Id == item.Id);

        Assert.Equal(code, read.Code);
        Assert.Equal("中心靜脈導管組", read.Name);
        Assert.Equal("5Fr x 20cm 雙腔", read.Specification);
        Assert.Equal("組", read.UnitOfMeasure);
        Assert.True(read.TracksLot);
        Assert.False(read.TracksExpiry);
        Assert.Equal(7, read.SafetyStockQty);
        Assert.False(read.IsDeleted);
        Assert.Null(read.DeletedAt);
        // D4：CreatedBy 來自 OracleTestDatabase.CreateContext 明確指定的 actor（不是 DbContext 的預設值）。
        Assert.Equal("integration-test", read.CreatedBy);
        Assert.True(read.CreatedAt > DateTime.UtcNow.AddMinutes(-5));

        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Item_name_stores_twenty_chinese_characters_intact()
    {
        // schema 的字串欄位是 VARCHAR2(n CHAR) 語意：ITEM_NAME 是 200 CHAR，裝得下 200 個中文字。
        // 若欄位（或對映）誤用 BYTE 語意，20 個中文字 = 60 bytes，會在超過位元組上限時噴 ORA-12899。
        // ★ 本專案一律使用虛構機構與通用名稱，不出現任何真實醫療院所的名字。
        //   這是作品集的法務與專業性考量，見 docs/requirements.md OUT-7。
        //   同時這個字串是「品項名稱」，所以用醫材品名而不是院區/科室名 —— 前一版誤用了科室名。
        const string name = "小兒血液腫瘤科重症加護病房專用無菌敷料包";
        Assert.Equal(20, name.Length);

        await using var ctx = OracleTestDatabase.CreateContext();
        await using var tx = await ctx.Database.BeginTransactionAsync();

        var item = new Item { Code = "IT-" + Suffix(), Name = name, UnitOfMeasure = "支" };
        ctx.Items.Add(item);
        await ctx.SaveChangesAsync();

        ctx.ChangeTracker.Clear();
        var read = await ctx.Items.AsNoTracking().SingleAsync(x => x.Id == item.Id);

        _output.WriteLine($"寫入 ({name.Length} 字)：{name}");
        _output.WriteLine($"讀回 ({read.Name.Length} 字)：{read.Name}");
        Assert.Equal(name, read.Name);
        Assert.Equal(20, read.Name.Length);

        await tx.RollbackAsync();
    }

    [Fact]
    public async Task StockLot_round_trips_including_DateOnly_expiry()
    {
        await using var ctx = OracleTestDatabase.CreateContext();
        await using var tx = await ctx.Database.BeginTransactionAsync();

        var itemId = await InsertItemAsync(ctx);

        var expiry = new DateOnly(2027, 3, 31);
        var lot = new StockLot(0, itemId, "LOT-" + Suffix(), expiry, 250, "A-12-3");
        ctx.StockLots.Add(lot);
        await ctx.SaveChangesAsync();

        Assert.True(lot.Id > 0);

        ctx.ChangeTracker.Clear();
        var read = await ctx.StockLots.AsNoTracking().SingleAsync(x => x.Id == lot.Id);

        Assert.Equal(itemId, read.ItemId);
        Assert.Equal(lot.LotNumber, read.LotNumber);
        Assert.Equal(expiry, read.ExpiryDate); // DateOnly <-> Oracle DATE 轉換器
        Assert.Equal(250, read.Quantity);
        Assert.Equal("A-12-3", read.StorageLocation);

        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Requisition_with_two_lines_round_trips_and_assigns_sequential_line_numbers()
    {
        await using var ctx = OracleTestDatabase.CreateContext();
        await using var tx = await ctx.Database.BeginTransactionAsync();

        var deptId = await InsertDepartmentAsync(ctx);
        var itemA = await InsertItemAsync(ctx);
        var itemB = await InsertItemAsync(ctx);

        var req = new Requisition(0, deptId);
        ctx.Entry(req).Property("RequisitionNo").CurrentValue = "REQ-" + Suffix();
        req.AddLine(new RequisitionLine(itemA, 5));
        req.AddLine(new RequisitionLine(itemB, 8));
        ctx.Requisitions.Add(req);
        await ctx.SaveChangesAsync();

        Assert.True(req.Id > 0);

        ctx.ChangeTracker.Clear();
        var read = await ctx.Requisitions.AsNoTracking()
            .Include(r => r.Lines)
            .SingleAsync(r => r.Id == req.Id);

        Assert.Equal(RequisitionStatus.Draft, read.Status);
        Assert.Equal(2, read.Lines.Count);

        var lines = await ctx.RequisitionLines.AsNoTracking()
            .Where(l => EF.Property<long>(l, "RequisitionId") == req.Id)
            .Select(l => new { l.ItemId, l.Quantity, LineNo = EF.Property<int>(l, "LineNo") })
            .OrderBy(x => x.LineNo)
            .ToListAsync();

        Assert.Equal(2, lines.Count);
        Assert.Equal(1, lines[0].LineNo);
        Assert.Equal(2, lines[1].LineNo);
        Assert.Equal((itemA, 5), (lines[0].ItemId, lines[0].Quantity));
        Assert.Equal((itemB, 8), (lines[1].ItemId, lines[1].Quantity));

        // STATUS 欄位在資料庫裡實際存的是字串 'Draft'，不是數字 —— 稽核可讀性的設計。
        var statusRaw = await ctx.Database
            .SqlQueryRaw<string>("SELECT status AS \"Value\" FROM requisitions WHERE requisition_id = {0}", req.Id)
            .SingleAsync();
        Assert.Equal("Draft", statusRaw);

        await tx.RollbackAsync();
    }

    [Fact]
    public async Task IssueAllocation_and_AuditLog_round_trip()
    {
        await using var ctx = OracleTestDatabase.CreateContext();
        await using var tx = await ctx.Database.BeginTransactionAsync();

        var itemId = await InsertItemAsync(ctx);
        var deptId = await InsertDepartmentAsync(ctx);

        var expiry = new DateOnly(2026, 12, 31);
        var lot = new StockLot(0, itemId, "LOT-" + Suffix(), expiry, 100, "B-1-1");
        ctx.StockLots.Add(lot);

        var req = new Requisition(0, deptId);
        ctx.Entry(req).Property("RequisitionNo").CurrentValue = "REQ-" + Suffix();
        req.AddLine(new RequisitionLine(itemId, 10));
        ctx.Requisitions.Add(req);
        await ctx.SaveChangesAsync();

        var lineId = await ctx.RequisitionLines
            .Where(l => EF.Property<long>(l, "RequisitionId") == req.Id)
            .Select(l => EF.Property<long>(l, "RequisitionLineId"))
            .SingleAsync();

        var alloc = new IssueAllocation
        {
            RequisitionLineId = lineId,
            StockLotId = lot.Id,
            Quantity = 3,
            ExpiryDateAtIssue = expiry,
            IssuedAt = DateTime.UtcNow,
            IssuedBy = "tester",
        };
        ctx.IssueAllocations.Add(alloc);

        var audit = new AuditLog
        {
            EntityType = "StockLot",
            EntityId = lot.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Action = "ISSUE",
            Actor = "tester",
            OccurredAt = DateTime.UtcNow,
            OldValue = "{\"quantity\":100}",
            NewValue = "{\"quantity\":97}",
        };
        ctx.AuditLogs.Add(audit);
        await ctx.SaveChangesAsync();

        ctx.ChangeTracker.Clear();

        var readAlloc = await ctx.IssueAllocations.AsNoTracking().SingleAsync(x => x.Id == alloc.Id);
        Assert.Equal(lineId, readAlloc.RequisitionLineId);
        Assert.Equal(lot.Id, readAlloc.StockLotId);
        Assert.Equal(3, readAlloc.Quantity);
        Assert.Equal(expiry, readAlloc.ExpiryDateAtIssue);
        Assert.Equal("tester", readAlloc.IssuedBy);

        var readAudit = await ctx.AuditLogs.AsNoTracking().SingleAsync(x => x.Id == audit.Id);
        Assert.Equal("StockLot", readAudit.EntityType);
        Assert.Equal("ISSUE", readAudit.Action);
        Assert.Equal("{\"quantity\":100}", readAudit.OldValue); // CLOB
        Assert.Equal("{\"quantity\":97}", readAudit.NewValue);

        await tx.RollbackAsync();
    }

    [Fact]
    public async Task AuditLog_rejects_application_updates_and_deletes()
    {
        await using var ctx = OracleTestDatabase.CreateContext();
        await using var tx = await ctx.Database.BeginTransactionAsync();
        var audit = new AuditLog
        {
            EntityType = "Requisition",
            EntityId = "append-only-probe",
            Action = "Create",
            Actor = "integration-test",
            OccurredAt = DateTime.UtcNow,
            NewValue = "{}",
        };
        ctx.AuditLogs.Add(audit);
        await ctx.SaveChangesAsync();

        audit.NewValue = "{\"changed\":true}";
        var updateError = await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.SaveChangesAsync());
        Assert.Contains("只允許新增", updateError.Message, StringComparison.Ordinal);

        ctx.Entry(audit).State = EntityState.Unchanged;
        ctx.AuditLogs.Remove(audit);
        var deleteError = await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.SaveChangesAsync());
        Assert.Contains("只允許新增", deleteError.Message, StringComparison.Ordinal);

        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Generated_SQL_targets_uppercase_table_and_column_names()
    {
        // T1：Oracle 未加引號的識別項會折成大寫，schema 裡的表實際叫 ITEMS。
        // 對映若沿用 C# 型別名（"Items"），EF 送出的 SQL 會查不到表（ORA-00942）。
        var captured = new List<string>();
        await using var ctx = OracleTestDatabase.CreateContext(captured.Add);
        await using var tx = await ctx.Database.BeginTransactionAsync();

        var code = "IT-" + Suffix();
        ctx.Items.Add(new Item { Code = code, Name = "SQL 探測用", UnitOfMeasure = "件" });
        await ctx.SaveChangesAsync();

        ctx.ChangeTracker.Clear();
        _ = await ctx.Items.AsNoTracking().Where(x => x.Code == code).ToListAsync();

        await tx.RollbackAsync();

        var sql = string.Join(Environment.NewLine, captured);
        _output.WriteLine(sql);

        Assert.Contains("ITEMS", sql, StringComparison.Ordinal);
        Assert.Contains("ITEM_CODE", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Items\"", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Item_Code\"", sql, StringComparison.Ordinal);
    }

    private static async Task<long> InsertItemAsync(MedSupplyOpsDbContext ctx)
    {
        var item = new Item { Code = "IT-" + Suffix(), Name = "夾具", UnitOfMeasure = "個" };
        ctx.Items.Add(item);
        await ctx.SaveChangesAsync();
        return item.Id;
    }

    private static async Task<long> InsertDepartmentAsync(MedSupplyOpsDbContext ctx)
    {
        var dept = new Department { Code = "D-" + Suffix(), Name = "重症加護病房" };
        ctx.Departments.Add(dept);
        await ctx.SaveChangesAsync();
        return dept.Id;
    }
}
