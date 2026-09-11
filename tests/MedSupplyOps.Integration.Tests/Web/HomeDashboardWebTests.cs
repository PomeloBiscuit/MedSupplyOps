using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using MedSupplyOps.Infrastructure.Queries;
using MedSupplyOps.Infrastructure.Time;
using MedSupplyOps.Web.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Oracle.ManagedDataAccess.Client;
using Xunit.Abstractions;

namespace MedSupplyOps.Integration.Tests.Web;

/// <summary>首頁工作儀表板的驗收案例 T1～T7。</summary>
public sealed class HomeDashboardWebTests : IClassFixture<RequisitionFlowTests.RequisitionWebApplicationFactory>
{
    private readonly RequisitionFlowTests.RequisitionWebApplicationFactory _factory;
    private readonly ITestOutputHelper _output;

    public HomeDashboardWebTests(RequisitionFlowTests.RequisitionWebApplicationFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    /// <summary>
    /// ★ T1：請領人看不到別科室的單；庫管員（不受限）的待審核數要 +1。
    /// 用示範／測試帳號自己點永遠測不到這件事：三個帳號的科室範圍規則本來就對，
    /// 會壞的是「沒有角色」的那條路徑（見 T2），不是這三個角色互相看得到彼此的資料。
    /// </summary>
    [Fact]
    public async Task Requester_does_not_see_other_departments_pending_requisition_while_keepers_count_increases()
    {
        var suffix = "T1" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var departmentCode = "D" + suffix;
        var requisitionNo = "R" + suffix;

        using var requesterClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var keeperClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await WebAuthTestHelpers.LoginAsync(requesterClient, TestIdentitySeeder.RequesterEmail);
        await WebAuthTestHelpers.LoginAsync(keeperClient, TestIdentitySeeder.StorekeeperEmail);

        var requesterBefore = ExtractCount(await GetHtmlAsync(requesterClient, "/"), "pending-approval-count");
        var keeperBefore = ExtractCount(await GetHtmlAsync(keeperClient, "/"), "pending-approval-count");

        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            "INSERT INTO departments (department_code, department_name, created_by) VALUES (:code, :name, 'itest')",
            new { code = departmentCode, name = "測試科室 " + suffix });
        var otherDepartmentId = await connection.ExecuteScalarAsync<long>(
            "SELECT department_id FROM departments WHERE department_code = :code", new { code = departmentCode });

        try
        {
            await connection.ExecuteAsync(
                "INSERT INTO requisitions (requisition_no, department_id, status, created_by) VALUES (:no, :deptId, 'PendingApproval', 'itest')",
                new { no = requisitionNo, deptId = otherDepartmentId });

            var requesterHtml = await GetHtmlAsync(requesterClient, "/");
            Assert.DoesNotContain(requisitionNo, requesterHtml, StringComparison.Ordinal);
            Assert.Equal(requesterBefore, ExtractCount(requesterHtml, "pending-approval-count"));

            var keeperHtml = await GetHtmlAsync(keeperClient, "/");
            Assert.Equal(keeperBefore + 1, ExtractCount(keeperHtml, "pending-approval-count"));

            _output.WriteLine($"T1 REQUESTER: 首頁未出現 {requisitionNo}；待審核卡片維持 {requesterBefore}。");
            _output.WriteLine($"T1 KEEPER: 待審核卡片 {keeperBefore} → {keeperBefore + 1}。");
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM audit_logs WHERE entity_type = 'Requisition' AND entity_id = (SELECT TO_CHAR(requisition_id) FROM requisitions WHERE requisition_no = :no)", new { no = requisitionNo });
            await connection.ExecuteAsync("DELETE FROM requisitions WHERE requisition_no = :no", new { no = requisitionNo });
            await connection.ExecuteAsync("DELETE FROM departments WHERE department_id = :id", new { id = otherDepartmentId });
            await connection.ExecuteAsync("COMMIT");
        }
    }

    /// <summary>
    /// ★ T2：沒有任何角色的帳號。這是這張單最重要的一題——三個示範／測試帳號永遠測不到它，
    /// 因為它們全部至少有一個角色；沒有角色是第四種、程式碼裡原本沒有名字的狀態，
    /// 只點三個帳號永遠不會踩到「沒有角色」這條路徑。
    /// </summary>
    [Fact]
    public async Task NoRole_account_sees_the_notice_without_any_dashboard_data()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await WebAuthTestHelpers.LoginAsync(client, TestIdentitySeeder.NoRoleEmail);

        var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Contains("尚未設定角色或科室", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-testid=\"pending-approval-count\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-testid=\"recent-audit-list\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-testid=\"recent-requisitions-table\"", html, StringComparison.Ordinal);

        var noticeMatch = Regex.Match(html, "data-testid=\"no-role-notice\"[^>]*>\\s*([^<]+?)\\s*<");
        Assert.True(noticeMatch.Success, "找不到沒有角色的提示訊息。");
        _output.WriteLine($"T2 HTTP={(int)response.StatusCode}；提示文字：{noticeMatch.Groups[1].Value.Trim()}");
        _output.WriteLine("T2 為什麼三個示範帳號點不到：requester/keeper/admin 各自至少有一個角色，"
            + "永遠落在「限科室」或「不受限」兩條路徑；「沒有角色」是第三條、程式碼裡沒有名字的路徑，"
            + "不專門造一個沒有角色的帳號就不會被執行到。");
        _output.WriteLine("D8 突變（把規則改回「不是請領人就不受限」）讓本測試變紅的輸出見 scripts/mutation-probe.ps1 執行紀錄。");
    }

    /// <summary>
    /// ★ T3：卡片數字必須等於點進去那一頁看到的數字。四張卡都用「新增一筆、卡片 +1、
    /// 且新增的那筆真的出現在連去的頁面」來驗證，而不是比對頁面的總筆數——
    /// 頁面本來就有種子資料與其他測試留下的資料，比總數會不穩定；比「同一筆資料兩邊都算」
    /// 才是 D3 真正要保證的事（不可以另寫一份「差不多」的查詢）。
    /// </summary>
    [Fact]
    public async Task Keeper_dashboard_card_numbers_match_the_pages_they_link_to()
    {
        var suffix = "T3" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        using var keeperClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await WebAuthTestHelpers.LoginAsync(keeperClient, TestIdentitySeeder.StorekeeperEmail);

        var before = await GetHtmlAsync(keeperClient, "/");
        var pendingBefore = ExtractCount(before, "pending-approval-count");
        var approvedBefore = ExtractCount(before, "approved-awaiting-issue-count");
        var expiringBefore = ExtractCount(before, "expiring-within-30-days-count");
        var belowSafetyBefore = ExtractCount(before, "below-safety-stock-count");

        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();

        var pendingNo = "RP" + suffix;
        var approvedNo = "RA" + suffix;
        var departmentCode = "D" + suffix;
        var expiringItemCode = "IE" + suffix;
        var expiringLotNumber = "LOT" + suffix;
        var belowSafetyItemCode = "IB" + suffix;

        await connection.ExecuteAsync(
            "INSERT INTO departments (department_code, department_name, created_by) VALUES (:code, :name, 'itest')",
            new { code = departmentCode, name = "測試科室 " + suffix });
        var departmentId = await connection.ExecuteScalarAsync<long>(
            "SELECT department_id FROM departments WHERE department_code = :code", new { code = departmentCode });

        try
        {
            await connection.ExecuteAsync(
                "INSERT INTO requisitions (requisition_no, department_id, status, created_by) VALUES (:no, :deptId, 'PendingApproval', 'itest')",
                new { no = pendingNo, deptId = departmentId });
            await connection.ExecuteAsync(
                "INSERT INTO requisitions (requisition_no, department_id, status, created_by) VALUES (:no, :deptId, 'Approved', 'itest')",
                new { no = approvedNo, deptId = departmentId });

            await connection.ExecuteAsync(
                "INSERT INTO items (item_code, item_name, unit_of_measure, safety_stock_qty, created_by) VALUES (:code, :name, '個', 0, 'itest')",
                new { code = expiringItemCode, name = "測試近效期品項 " + suffix });
            var expiringItemId = await connection.ExecuteScalarAsync<long>(
                "SELECT item_id FROM items WHERE item_code = :code", new { code = expiringItemCode });
            await connection.ExecuteAsync(
                "INSERT INTO stock_lots (item_id, lot_number, expiry_date, quantity, storage_location, created_by) VALUES (:itemId, :lot, TRUNC(SYSDATE) + 10, 5, 'ITEST-A01', 'itest')",
                new { itemId = expiringItemId, lot = expiringLotNumber });

            await connection.ExecuteAsync(
                "INSERT INTO items (item_code, item_name, unit_of_measure, safety_stock_qty, created_by) VALUES (:code, :name, '個', 10, 'itest')",
                new { code = belowSafetyItemCode, name = "測試低庫存品項 " + suffix });

            using var scope = _factory.Services.CreateScope();
            var businessCalendar = scope.ServiceProvider.GetRequiredService<BusinessCalendar>();
            var inventoryQueries = scope.ServiceProvider.GetRequiredService<InventoryQueries>();
            var today = businessCalendar.Today;

            var html = await GetHtmlAsync(keeperClient, "/");
            var pendingAfter = ExtractCount(html, "pending-approval-count");
            var approvedAfter = ExtractCount(html, "approved-awaiting-issue-count");
            var expiringAfter = ExtractCount(html, "expiring-within-30-days-count");
            var belowSafetyAfter = ExtractCount(html, "below-safety-stock-count");

            Assert.Equal(pendingBefore + 1, pendingAfter);
            Assert.Equal(approvedBefore + 1, approvedAfter);
            Assert.Equal(expiringBefore + 1, expiringAfter);
            Assert.Equal(belowSafetyBefore + 1, belowSafetyAfter);

            // 用產品實際呼叫的查詢方法確認卡片背後跟頁面背後是同一份資料，不是兩份「差不多」的定義。
            var expiringLots = await inventoryQueries.GetExpiringLotsAsync(30, today);
            Assert.Contains(expiringLots, lot => lot.LotNumber == expiringLotNumber);
            var belowSafety = await inventoryQueries.GetItemsBelowSafetyStockAsync(today);
            Assert.Contains(belowSafety, item => item.ItemCode == belowSafetyItemCode);

            var pendingPage = await GetHtmlAsync(keeperClient, "/Requisitions?status=PendingApproval");
            Assert.Contains(pendingNo, pendingPage, StringComparison.Ordinal);
            var approvedPage = await GetHtmlAsync(keeperClient, "/Requisitions?status=Approved");
            Assert.Contains(approvedNo, approvedPage, StringComparison.Ordinal);
            var expiringPage = await GetHtmlAsync(keeperClient, "/Inventory/Expiring?withinDays=30");
            Assert.Contains(expiringLotNumber, expiringPage, StringComparison.Ordinal);
            var inventoryPage = await GetHtmlAsync(keeperClient, "/Inventory");
            Assert.Contains(belowSafetyItemCode, inventoryPage, StringComparison.Ordinal);

            _output.WriteLine("T3 對照表（卡片｜頁面實際出現）：");
            _output.WriteLine($"  待審核        {pendingBefore}→{pendingAfter} ｜ /Requisitions?status=PendingApproval 含 {pendingNo}");
            _output.WriteLine($"  待發料        {approvedBefore}→{approvedAfter} ｜ /Requisitions?status=Approved 含 {approvedNo}");
            _output.WriteLine($"  30天內到期    {expiringBefore}→{expiringAfter} ｜ /Inventory/Expiring?withinDays=30 含 {expiringLotNumber}");
            _output.WriteLine($"  低於安全存量  {belowSafetyBefore}→{belowSafetyAfter} ｜ /Inventory 含 {belowSafetyItemCode}");
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM stock_lots WHERE lot_number = :lot", new { lot = expiringLotNumber });
            await connection.ExecuteAsync("DELETE FROM items WHERE item_code IN (:a, :b)", new { a = expiringItemCode, b = belowSafetyItemCode });
            await connection.ExecuteAsync("DELETE FROM requisitions WHERE requisition_no IN (:a, :b)", new { a = pendingNo, b = approvedNo });
            await connection.ExecuteAsync("DELETE FROM departments WHERE department_id = :id", new { id = departmentId });
            await connection.ExecuteAsync("COMMIT");
        }
    }

    /// <summary>
    /// ★ T4：「本月」必須用台灣時區的月份邊界，不是 UTC 的月份邊界。
    /// 把時鐘撥到 UTC 2030-06-30 16:30（台灣 2030-07-01 00:30），
    /// A 單發料於台灣 6/30 23:30、B 單發料於台灣 7/1 00:10 —— 只有 B 算「本月」。
    /// 若用 UTC 月份切，A 與 B 的 issued_at 都還在 UTC 6 月，兩張都會被算進「本月」，
    /// 答案會是 2，而不是正確的 1。
    /// </summary>
    [Fact]
    public async Task Requester_dashboard_counts_issued_this_month_by_taipei_month_boundary()
    {
        var boundary = new DateTimeOffset(2030, 6, 30, 16, 30, 0, TimeSpan.Zero); // 台灣 2030-07-01 00:30
        TestBusinessCalendar.HostClock.UtcNow = boundary;
        try
        {
            using var requesterClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            await WebAuthTestHelpers.LoginAsync(requesterClient, TestIdentitySeeder.RequesterEmail);

            using var scope = _factory.Services.CreateScope();
            var businessCalendar = scope.ServiceProvider.GetRequiredService<BusinessCalendar>();
            var (fromUtc, toUtc) = businessCalendar.CurrentMonthRangeUtc();
            _output.WriteLine($"T4 BusinessCalendar.CurrentMonthRangeUtc() = [{fromUtc:O}, {toUtc:O})");

            await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
            await connection.OpenAsync();
            var requesterDepartmentId = await connection.ExecuteScalarAsync<long>(
                "SELECT department_id FROM departments WHERE department_code = :code",
                new { code = TestIdentitySeeder.RequesterDepartmentCode });

            const string countSql = """
                SELECT COUNT(*) FROM requisitions
                WHERE department_id = :deptId AND issued_at >= :fromUtc AND issued_at < :toUtc
                """;
            var baseline = await connection.ExecuteScalarAsync<int>(countSql, new { deptId = requesterDepartmentId, fromUtc, toUtc });

            var suffix = "T4" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            var noA = "RA" + suffix;
            var noB = "RB" + suffix;

            try
            {
                await connection.ExecuteAsync("""
                    INSERT INTO requisitions (requisition_no, department_id, status, issued_at, created_by)
                    VALUES (:no, :deptId, 'Issued', TIMESTAMP '2030-06-30 15:30:00', 'itest')
                    """, new { no = noA, deptId = requesterDepartmentId });
                await connection.ExecuteAsync("""
                    INSERT INTO requisitions (requisition_no, department_id, status, issued_at, created_by)
                    VALUES (:no, :deptId, 'Issued', TIMESTAMP '2030-06-30 16:10:00', 'itest')
                    """, new { no = noB, deptId = requesterDepartmentId });

                var expected = await connection.ExecuteScalarAsync<int>(countSql, new { deptId = requesterDepartmentId, fromUtc, toUtc });
                Assert.Equal(baseline + 1, expected); // 只有 B（issued_at 2030-06-30 16:10 UTC，落在台灣 7/1）算本月

                var html = await GetHtmlAsync(requesterClient, "/");
                var issuedThisMonth = ExtractCount(html, "issued-this-month-count");
                Assert.Equal(expected, issuedThisMonth);

                var utcMonthCount = 2; // A 與 B 的 issued_at 都還在 UTC 6 月，若用 UTC 月份切兩張都會被算進去。
                _output.WriteLine($"T4 本月已發料卡片＝{issuedThisMonth}（正確答案：只有 B）；若改用 UTC 月份切會是基準 {baseline} + {utcMonthCount} = {baseline + utcMonthCount}。");
            }
            finally
            {
                await connection.ExecuteAsync("DELETE FROM requisitions WHERE requisition_no IN (:a, :b)", new { a = noA, b = noB });
                await connection.ExecuteAsync("COMMIT");
            }
        }
        finally
        {
            TestBusinessCalendar.HostClock.UtcNow = TestBusinessCalendar.DefaultInstant;
        }
    }

    /// <summary>
    /// ★ T5：稽核列表的排序與容錯。同一秒的兩筆用 audit_log_id DESC 決勝（重複取兩次順序要一致），
    /// 顯示姓名找得到就用 display_name，不認得的 entity_type 顯示「{entity_type} #{entity_id}」
    /// 而不是隱藏或丟例外，時間用台灣時間。
    /// </summary>
    [Fact]
    public async Task Recent_audit_list_breaks_same_second_ties_by_id_and_shows_unknown_entity_types()
    {
        var suffix = "T5" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var departmentCode = "D" + suffix;
        var requisitionNo = "R" + suffix;
        var occurredAt = new DateTime(2030, 1, 1, 10, 0, 0, DateTimeKind.Utc); // 台灣 2030-01-01 18:00

        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            "INSERT INTO departments (department_code, department_name, created_by) VALUES (:code, :name, 'itest')",
            new { code = departmentCode, name = "測試科室 " + suffix });
        var departmentId = await connection.ExecuteScalarAsync<long>(
            "SELECT department_id FROM departments WHERE department_code = :code", new { code = departmentCode });
        await connection.ExecuteAsync(
            "INSERT INTO requisitions (requisition_no, department_id, status, created_by) VALUES (:no, :deptId, 'Draft', 'itest')",
            new { no = requisitionNo, deptId = departmentId });
        var requisitionId = await connection.ExecuteScalarAsync<long>(
            "SELECT requisition_id FROM requisitions WHERE requisition_no = :no", new { no = requisitionNo });

        try
        {
            // 先插入的那筆 audit_log_id 較小；同一秒下，較晚插入的 Mystery 那筆 id 較大，排序要在前面。
            await connection.ExecuteAsync(
                "INSERT INTO audit_logs (entity_type, entity_id, action, actor, occurred_at) VALUES ('Requisition', :id, 'Create', :actor, :t)",
                new { id = requisitionId.ToString(CultureInfo.InvariantCulture), actor = TestIdentitySeeder.StorekeeperEmail, t = occurredAt });
            await connection.ExecuteAsync(
                "INSERT INTO audit_logs (entity_type, entity_id, action, actor, occurred_at) VALUES ('Mystery', '424242', 'Poke', :actor, :t)",
                new { actor = TestIdentitySeeder.StorekeeperEmail, t = occurredAt });
            await connection.ExecuteAsync(
                "INSERT INTO audit_logs (entity_type, entity_id, action, actor, occurred_at) VALUES ('Requisition', :id, 'Approve', :actor, :t)",
                new
                {
                    id = requisitionId.ToString(CultureInfo.InvariantCulture),
                    actor = TestIdentitySeeder.StorekeeperEmail,
                    t = occurredAt.AddMinutes(1),
                });

            using var keeperClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            await WebAuthTestHelpers.LoginAsync(keeperClient, TestIdentitySeeder.StorekeeperEmail);

            var firstFetch = ExtractAuditListItems(await GetHtmlAsync(keeperClient, "/"));
            var secondFetch = ExtractAuditListItems(await GetHtmlAsync(keeperClient, "/"));

            Assert.Equal(firstFetch, secondFetch); // 順序必須穩定，重複取兩次一致

            Assert.Contains("測試庫管員 核准了 請領單 " + requisitionNo, firstFetch[0], StringComparison.Ordinal);
            Assert.Contains("01-01 18:01", firstFetch[0], StringComparison.Ordinal);

            // 同一秒：Mystery（後插入、audit_log_id 較大）必須排在 Requisition/Create（先插入）前面。
            Assert.Contains("Mystery #424242", firstFetch[1], StringComparison.Ordinal);
            Assert.Contains("測試庫管員 Poke了", firstFetch[1], StringComparison.Ordinal);
            Assert.Contains("01-01 18:00", firstFetch[1], StringComparison.Ordinal);

            Assert.Contains("測試庫管員 建立了 請領單 " + requisitionNo, firstFetch[2], StringComparison.Ordinal);
            Assert.Contains("01-01 18:00", firstFetch[2], StringComparison.Ordinal);

            _output.WriteLine("T5 最近異動列表（前三筆）：");
            for (var i = 0; i < 3; i++)
            {
                _output.WriteLine($"  [{i}] {firstFetch[i]}");
            }
        }
        finally
        {
            await connection.ExecuteAsync(
                "DELETE FROM audit_logs WHERE (entity_type = 'Requisition' AND entity_id = :id) OR (entity_type = 'Mystery' AND entity_id = '424242')",
                new { id = requisitionId.ToString(CultureInfo.InvariantCulture) });
            await connection.ExecuteAsync("DELETE FROM requisitions WHERE requisition_no = :no", new { no = requisitionNo });
            await connection.ExecuteAsync("DELETE FROM departments WHERE department_id = :id", new { id = departmentId });
            await connection.ExecuteAsync("COMMIT");
        }
    }

    /// <summary>
    /// ★ 「沒有範圍」必須對**每一個**用 <c>IsRestricted</c> 判斷的地方都是「限定到沒有科室」，不是「不受限」（L-029）。
    ///
    /// <c>RequisitionsController</c> 的每一處都只看 <c>IsRestricted</c>。若 <c>NoScope.IsRestricted</c> 是 false，
    /// 它在那裡就等於全院。今天那些 Action 被三角色 Policy 擋住所以看不到 ——
    /// 哪天有人把新角色加進 <c>RequisitionRead</c>，那個角色就會看到全院的請領單，而畫面完全正常。
    /// </summary>
    [Fact]
    public void NoScope_fails_closed_wherever_IsRestricted_is_checked()
    {
        Assert.True(DepartmentScope.NoScope.IsRestricted, "NoScope 必須算「受限」，否則只看 IsRestricted 的地方會把它當成全院。");
        Assert.Null(DepartmentScope.NoScope.DepartmentId);
        Assert.False(DepartmentScope.Unrestricted.IsRestricted);
        Assert.True(DepartmentScope.RestrictedTo(3).IsRestricted);
    }

    /// <summary>
    /// ★ <c>audit_logs.entity_id</c> 是字串欄位（L-029）。
    /// 儀表板若在 JOIN 條件裡直接 <c>TO_NUMBER(entity_id)</c>，Oracle 不保證先比對 <c>entity_type</c> ——
    /// 一筆非數字 id 的稽核（例如將來以 GUID 為鍵的類型）就可能讓營運儀表板整頁 ORA-01722。
    /// D3 的要求是：不認得的類型照樣顯示，首頁不能因此壞掉。
    /// </summary>
    [Fact]
    public async Task Audit_feed_survives_a_non_numeric_entity_id()
    {
        var entityId = "GUID-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            "INSERT INTO audit_logs (entity_type, entity_id, action, actor, occurred_at) VALUES ('Mystery', :entityId, 'Poke', :actor, :t)",
            new { entityId, actor = TestIdentitySeeder.StorekeeperEmail, t = new DateTime(2031, 1, 1, 0, 0, 0) });
        try
        {
            using var keeperClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            await WebAuthTestHelpers.LoginAsync(keeperClient, TestIdentitySeeder.StorekeeperEmail);

            var response = await keeperClient.GetAsync("/");
            var html = await response.Content.ReadAsStringAsync();
            _output.WriteLine($"非數字 entity_id {entityId}：HTTP {(int)response.StatusCode}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("Mystery #" + entityId, html, StringComparison.Ordinal);
        }
        finally
        {
            await connection.ExecuteAsync(
                "DELETE FROM audit_logs WHERE entity_type = 'Mystery' AND entity_id = :entityId",
                new { entityId });
        }
    }

    /// <summary>
    /// ★ T6：「已過期仍在庫」只算數量 &gt; 0、品項未停用的過期批次。
    /// 種子資料 GLO-EXPIRED-01（過期、量 50）本來就會讓這張卡在乾淨資料庫上出現；
    /// 這裡另外加三筆，只有「過期且數量 &gt; 0、品項未停用」那一筆會讓卡片 +1。
    /// </summary>
    [Fact]
    public async Task Expired_in_stock_alert_only_counts_lots_with_quantity_and_active_item()
    {
        var suffix = "T6" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        using var keeperClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await WebAuthTestHelpers.LoginAsync(keeperClient, TestIdentitySeeder.StorekeeperEmail);
        var before = ExtractCount(await GetHtmlAsync(keeperClient, "/"), "expired-in-stock-count");

        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();

        var activeCode = "IA" + suffix; // 過期、量 50、未停用 → 算
        var zeroQtyCode = "IZ" + suffix; // 過期、量 0 → 不算
        var deletedCode = "ID" + suffix; // 過期、量 50、已停用 → 不算

        await connection.ExecuteAsync(
            "INSERT INTO items (item_code, item_name, unit_of_measure, created_by) VALUES (:code, '測試過期品項', '個', 'itest')",
            new { code = activeCode });
        await connection.ExecuteAsync(
            "INSERT INTO items (item_code, item_name, unit_of_measure, created_by) VALUES (:code, '測試過期零量品項', '個', 'itest')",
            new { code = zeroQtyCode });
        await connection.ExecuteAsync(
            "INSERT INTO items (item_code, item_name, unit_of_measure, is_deleted, deleted_at, deleted_by, created_by) VALUES (:code, '測試已停用過期品項', '個', 1, SYS_EXTRACT_UTC(SYSTIMESTAMP), 'itest', 'itest')",
            new { code = deletedCode });

        var activeId = await connection.ExecuteScalarAsync<long>("SELECT item_id FROM items WHERE item_code = :code", new { code = activeCode });
        var zeroQtyId = await connection.ExecuteScalarAsync<long>("SELECT item_id FROM items WHERE item_code = :code", new { code = zeroQtyCode });
        var deletedId = await connection.ExecuteScalarAsync<long>("SELECT item_id FROM items WHERE item_code = :code", new { code = deletedCode });

        try
        {
            await connection.ExecuteAsync(
                "INSERT INTO stock_lots (item_id, lot_number, expiry_date, quantity, storage_location, created_by) VALUES (:itemId, :lot, TRUNC(SYSDATE) - 5, 50, 'ITEST-A01', 'itest')",
                new { itemId = activeId, lot = "L1" + suffix });
            await connection.ExecuteAsync(
                "INSERT INTO stock_lots (item_id, lot_number, expiry_date, quantity, storage_location, created_by) VALUES (:itemId, :lot, TRUNC(SYSDATE) - 5, 0, 'ITEST-A01', 'itest')",
                new { itemId = zeroQtyId, lot = "L2" + suffix });
            await connection.ExecuteAsync(
                "INSERT INTO stock_lots (item_id, lot_number, expiry_date, quantity, storage_location, created_by) VALUES (:itemId, :lot, TRUNC(SYSDATE) - 5, 50, 'ITEST-A01', 'itest')",
                new { itemId = deletedId, lot = "L3" + suffix });

            var after = ExtractCount(await GetHtmlAsync(keeperClient, "/"), "expired-in-stock-count");
            Assert.Equal(before + 1, after);
            _output.WriteLine($"T6 已過期仍在庫卡片：{before} → {after}（新增：過期有量算 1、過期零量不算、過期但已停用不算）。");
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM stock_lots WHERE item_id IN (:a, :b, :c)", new { a = activeId, b = zeroQtyId, c = deletedId });
            await connection.ExecuteAsync("DELETE FROM items WHERE item_id IN (:a, :b, :c)", new { a = activeId, b = zeroQtyId, c = deletedId });
            await connection.ExecuteAsync("COMMIT");
        }
    }

    /// <summary>★ T7：駁回的請領單，請領人的首頁要看得到駁回原因。</summary>
    [Fact]
    public async Task Requester_dashboard_shows_the_rejection_reason_for_a_rejected_requisition()
    {
        var suffix = "T7" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var requisitionNo = "R" + suffix;
        const string reason = "庫存不足，請改叫貨";

        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        var requesterDepartmentId = await connection.ExecuteScalarAsync<long>(
            "SELECT department_id FROM departments WHERE department_code = :code",
            new { code = TestIdentitySeeder.RequesterDepartmentCode });

        try
        {
            await connection.ExecuteAsync("""
                INSERT INTO requisitions (requisition_no, department_id, status, rejection_reason, created_at, created_by)
                VALUES (:no, :deptId, 'Rejected', :reason, TIMESTAMP '2030-01-01 00:00:00', 'itest')
                """, new { no = requisitionNo, deptId = requesterDepartmentId, reason });

            using var requesterClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            await WebAuthTestHelpers.LoginAsync(requesterClient, TestIdentitySeeder.RequesterEmail);
            var html = await GetHtmlAsync(requesterClient, "/");

            Assert.Contains(requisitionNo, html, StringComparison.Ordinal);
            Assert.Contains("已駁回", html, StringComparison.Ordinal);
            Assert.Contains(reason, html, StringComparison.Ordinal);
            _output.WriteLine($"T7 首頁該列：單號 {requisitionNo}；狀態「已駁回」；駁回原因「{reason}」皆已出現。");
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM requisitions WHERE requisition_no = :no", new { no = requisitionNo });
            await connection.ExecuteAsync("COMMIT");
        }
    }

    /// <summary>
    /// 讀取頁面內容並解碼 HTML 實體。.NET 的預設 HtmlEncoder 只把 ASCII 視為安全字元，
    /// 中文字一律編碼成 &amp;#xXXXX; 數字實體——直接對原始回應內容比對中文字串必定比不到。
    /// </summary>
    private static async Task<string> GetHtmlAsync(HttpClient client, string path)
        => WebUtility.HtmlDecode(await client.GetStringAsync(path));

    private static int ExtractCount(string html, string testId)
    {
        var match = Regex.Match(html, $"data-testid=\"{Regex.Escape(testId)}\">\\s*(-?\\d+)\\s*<");
        Assert.True(match.Success, $"首頁找不到 {testId} 的卡片數字。");
        return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>擷取「最近異動」列表的每個 &lt;li&gt; 內文（去除標籤），依畫面順序排列。</summary>
    private static List<string> ExtractAuditListItems(string html)
    {
        var listMatch = Regex.Match(html, "data-testid=\"recent-audit-list\"[\\s\\S]*?</ul>");
        Assert.True(listMatch.Success, "首頁找不到最近異動列表。");
        var items = Regex.Matches(listMatch.Value, "<li[^>]*>([\\s\\S]*?)</li>")
            .Select(m => Regex.Replace(m.Groups[1].Value, "<[^>]+>", " "))
            .Select(text => Regex.Replace(WebUtility.HtmlDecode(text), "\\s+", " ").Trim())
            .ToList();
        return items;
    }
}
