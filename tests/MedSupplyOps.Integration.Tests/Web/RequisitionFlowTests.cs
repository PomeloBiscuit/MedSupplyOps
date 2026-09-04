using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using MedSupplyOps.Domain.Requisitions;
using MedSupplyOps.Infrastructure.Persistence;
using MedSupplyOps.Infrastructure.Queries;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Oracle.ManagedDataAccess.Client;
using Xunit.Abstractions;

namespace MedSupplyOps.Integration.Tests.Web;

public sealed partial class RequisitionFlowTests
    : IClassFixture<RequisitionFlowTests.RequisitionWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client;
    private readonly ITestOutputHelper _output;

    public RequisitionFlowTests(RequisitionWebApplicationFactory factory, ITestOutputHelper output)
    {
        _output = output;
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
    }

    /// <summary>
    /// ★ 設計裁定 D4：CreatedBy／UpdatedBy 不可以有靜默預設值，所以會寫資料的 action
    /// 一定要有登入者才能成功。這裡改用真正的 /Account/Login 端點登入示範帳號 keeper@example.local，
    /// 而不是繞過驗證塞 Cookie——理由與改動範圍見當時的 commit 訊息。
    /// </summary>
    public Task InitializeAsync() => WebAuthTestHelpers.LoginAsync(_client, "keeper@example.local");

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Create_submit_approve_completes_full_flow()
    {
        var id = await CreatePendingAsync();
        try
        {
            var details = await GetDetailsFormAsync(id);
            var response = await PostReviewAsync("Approve", id, details.RowVersion, details.Token);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            var result = await GetRequisitionAsync(id);
            Assert.Equal("Approved", result.Status);
            Assert.Equal(1m, result.RowVersion);
            Assert.Null(result.RejectionReason);
        }
        finally
        {
            await DeleteRequisitionsAsync([id]);
        }
    }

    [Fact]
    public async Task Create_submit_reject_completes_full_flow()
    {
        var id = await CreatePendingAsync();
        try
        {
            var details = await GetDetailsFormAsync(id);
            const string reason = "申請數量需重新確認";
            var response = await PostReviewAsync("Reject", id, details.RowVersion, details.Token, reason);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            var result = await GetRequisitionAsync(id);
            Assert.Equal("Rejected", result.Status);
            Assert.Equal(1m, result.RowVersion);
            Assert.Equal(reason, result.RejectionReason);
        }
        finally
        {
            await DeleteRequisitionsAsync([id]);
        }
    }

    [Fact]
    public async Task Illegal_approve_after_approved_is_rejected_with_clear_message()
    {
        var id = await CreatePendingAsync();
        try
        {
            var details = await GetDetailsFormAsync(id);
            _ = await PostReviewAsync("Approve", id, details.RowVersion, details.Token);

            var createPage = await _client.GetAsync("/Requisitions/Create?asOf=2026-09-01");
            var token = ExtractToken(await createPage.Content.ReadAsStringAsync());
            var response = await PostReviewAsync("Approve", id, rowVersion: 1, token);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            var redirectedPage = await _client.GetAsync(response.Headers.Location);
            var html = WebUtility.HtmlDecode(await redirectedPage.Content.ReadAsStringAsync());

            Assert.Contains("此單目前為「已核准」，無法核准。", html, StringComparison.Ordinal);
            Assert.Equal("Approved", (await GetRequisitionAsync(id)).Status);
        }
        finally
        {
            await DeleteRequisitionsAsync([id]);
        }
    }

    [Fact]
    public async Task Stale_approve_after_reject_reports_concurrency_and_keeps_rejected()
    {
        var id = await CreatePendingAsync();
        try
        {
            var details = await GetDetailsFormAsync(id);
            _ = await PostReviewAsync("Reject", id, details.RowVersion, details.Token, "已由另一位庫管員駁回");

            var staleResponse = await PostReviewAsync("Approve", id, details.RowVersion, details.Token);
            var redirectedPage = await _client.GetAsync(staleResponse.Headers.Location);
            var html = WebUtility.HtmlDecode(await redirectedPage.Content.ReadAsStringAsync());
            var result = await GetRequisitionAsync(id);

            Assert.Contains("<div class=\"alert alert-danger\" role=\"alert\">此單已被他人處理，請重新整理後再試。</div>", html, StringComparison.Ordinal);
            Assert.Equal("Rejected", result.Status);
            Assert.Equal(1m, result.RowVersion);
            _output.WriteLine("HTML: <div class=\"alert alert-danger\" role=\"alert\">此單已被他人處理，請重新整理後再試。</div>");
            _output.WriteLine($"DB: STATUS={result.Status}, ROW_VERSION={result.RowVersion}");
        }
        finally
        {
            await DeleteRequisitionsAsync([id]);
        }
    }

    [Fact]
    public async Task Ten_parallel_creates_produce_ten_distinct_numbers_without_unique_failures()
    {
        var createPage = await _client.GetAsync("/Requisitions/Create?asOf=2026-09-01");
        var token = ExtractToken(await createPage.Content.ReadAsStringAsync());
        var (departmentId, itemId) = await GetSeedIdsAsync();

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => _client.PostAsync("/Requisitions/Create", CreateForm(token, departmentId, itemId, 1)))
            .ToList();
        var responses = await Task.WhenAll(tasks);

        var ids = responses.Select(response =>
        {
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            return ExtractId(response.Headers.Location);
        }).ToList();

        try
        {
            await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
            var numbers = (await connection.QueryAsync<string>(
                "SELECT requisition_no FROM requisitions WHERE requisition_id IN :ids ORDER BY requisition_no",
                new { ids })).ToList();

            Assert.Equal(10, numbers.Count);
            Assert.Equal(10, numbers.Distinct(StringComparer.Ordinal).Count());
            _output.WriteLine($"DISTINCT={numbers.Distinct(StringComparer.Ordinal).Count()}, ORA-00001=0");
            foreach (var number in numbers)
            {
                _output.WriteLine(number);
            }
        }
        finally
        {
            await DeleteRequisitionsAsync(ids);
        }
    }

    [Fact]
    public async Task Approve_without_antiforgery_token_is_rejected_with_400()
    {
        var id = await CreatePendingAsync();
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["rowVersion"] = "0",
            });
            var response = await _client.PostAsync($"/Requisitions/Approve/{id}", content);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("PendingApproval", (await GetRequisitionAsync(id)).Status);
            _output.WriteLine($"HTTP={(int)response.StatusCode} {response.ReasonPhrase}");
            _output.WriteLine(await response.Content.ReadAsStringAsync());
        }
        finally
        {
            await DeleteRequisitionsAsync([id]);
        }
    }

    [Fact]
    public async Task Duplicate_item_returns_friendly_message_and_persists_nothing()
    {
        var createPage = await _client.GetAsync("/Requisitions/Create?asOf=2026-09-01");
        var token = ExtractToken(await createPage.Content.ReadAsStringAsync());
        var (departmentId, itemId) = await GetSeedIdsAsync();
        var before = await CountRequisitionsAsync();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["AsOf"] = "2026-09-01",
            ["DepartmentId"] = departmentId.ToString(CultureInfo.InvariantCulture),
            ["Lines[0].ItemId"] = itemId.ToString(CultureInfo.InvariantCulture),
            ["Lines[0].Quantity"] = "1",
            ["Lines[1].ItemId"] = itemId.ToString(CultureInfo.InvariantCulture),
            ["Lines[1].Quantity"] = "2",
        });

        var response = await _client.PostAsync("/Requisitions/Create", content);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("同一張請領單不可重複加入相同品項，請刪除重複明細。", html, StringComparison.Ordinal);
        Assert.DoesNotContain("ORA-00001", html, StringComparison.Ordinal);
        Assert.Equal(before, await CountRequisitionsAsync());
        _output.WriteLine("DB: ORA-00001 / UQ_REQ_LINES_ITEM");
        _output.WriteLine("USER: 同一張請領單不可重複加入相同品項，請刪除重複明細。");
    }

    [Theory]
    [InlineData(false, 1, "請領單至少需要一筆明細。")]
    [InlineData(true, 0, "請領數量必須為正整數。")]
    public async Task Empty_lines_and_zero_quantity_are_rejected(bool includeLine, int quantity, string expectedMessage)
    {
        var createPage = await _client.GetAsync("/Requisitions/Create?asOf=2026-09-01");
        var token = ExtractToken(await createPage.Content.ReadAsStringAsync());
        var (departmentId, itemId) = await GetSeedIdsAsync();
        var values = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["AsOf"] = "2026-09-01",
            ["DepartmentId"] = departmentId.ToString(CultureInfo.InvariantCulture),
        };
        if (includeLine)
        {
            values["Lines[0].ItemId"] = itemId.ToString(CultureInfo.InvariantCulture);
            values["Lines[0].Quantity"] = quantity.ToString(CultureInfo.InvariantCulture);
        }

        var before = await CountRequisitionsAsync();
        var response = await _client.PostAsync("/Requisitions/Create", new FormUrlEncodedContent(values));
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(expectedMessage, html, StringComparison.Ordinal);
        Assert.Equal(before, await CountRequisitionsAsync());
        _output.WriteLine($"HTTP={(int)response.StatusCode}; MESSAGE={expectedMessage}; PERSISTED=0");
    }

    [Fact]
    public async Task Database_allows_empty_header_but_rejects_zero_quantity_line()
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var (departmentId, itemId) = await GetSeedIdsAsync();
        var requisitionNo = "REQ-DB-PROBE-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

        try
        {
            await connection.ExecuteAsync(
                "INSERT INTO requisitions (requisition_no, department_id, status, created_by) VALUES (:requisitionNo, :departmentId, 'Draft', 'test')",
                new { requisitionNo, departmentId },
                transaction);
            var requisitionId = await connection.QuerySingleAsync<long>(
                "SELECT requisition_id FROM requisitions WHERE requisition_no = :requisitionNo",
                new { requisitionNo },
                transaction);
            var lineCount = await connection.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM requisition_lines WHERE requisition_id = :requisitionId",
                new { requisitionId },
                transaction);

            Assert.Equal(0, lineCount);
            _output.WriteLine($"DB EMPTY HEADER: INSERTED=1, LINE_COUNT={lineCount} (schema has no cross-table constraint)");

            var exception = await Assert.ThrowsAsync<OracleException>(() => connection.ExecuteAsync(
                "INSERT INTO requisition_lines (requisition_id, line_no, item_id, quantity) VALUES (:requisitionId, 1, :itemId, 0)",
                new { requisitionId, itemId },
                transaction));
            Assert.Equal(2290, exception.Number);
            Assert.Contains("CK_REQ_LINES_QTY", exception.Message, StringComparison.OrdinalIgnoreCase);
            _output.WriteLine($"DB ZERO QUANTITY: ORA-{exception.Number:D5}, CONSTRAINT=CK_REQ_LINES_QTY");
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private async Task<long> CreatePendingAsync()
    {
        var createPage = await _client.GetAsync("/Requisitions/Create?asOf=2026-09-01");
        var token = ExtractToken(await createPage.Content.ReadAsStringAsync());
        var (departmentId, itemId) = await GetSeedIdsAsync();
        var response = await _client.PostAsync("/Requisitions/Create", CreateForm(token, departmentId, itemId, 1));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return ExtractId(response.Headers.Location);
    }

    private async Task<(string Token, long RowVersion)> GetDetailsFormAsync(long id)
    {
        var response = await _client.GetAsync($"/Requisitions/Details/{id}");
        var html = await response.Content.ReadAsStringAsync();
        var token = ExtractToken(html);
        var rowVersion = long.Parse(RowVersionRegex().Match(html).Groups[1].Value, CultureInfo.InvariantCulture);
        return (token, rowVersion);
    }

    private Task<HttpResponseMessage> PostReviewAsync(
        string action,
        long id,
        long rowVersion,
        string token,
        string? rejectionReason = null)
    {
        var values = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["rowVersion"] = rowVersion.ToString(CultureInfo.InvariantCulture),
        };
        if (rejectionReason is not null)
        {
            values["rejectionReason"] = rejectionReason;
        }

        return _client.PostAsync($"/Requisitions/{action}/{id}", new FormUrlEncodedContent(values));
    }

    private static FormUrlEncodedContent CreateForm(string token, long departmentId, long itemId, int quantity)
        => new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["AsOf"] = "2026-09-01",
            ["DepartmentId"] = departmentId.ToString(CultureInfo.InvariantCulture),
            ["Lines[0].ItemId"] = itemId.ToString(CultureInfo.InvariantCulture),
            ["Lines[0].Quantity"] = quantity.ToString(CultureInfo.InvariantCulture),
        });

    private static async Task<(long DepartmentId, long ItemId)> GetSeedIdsAsync()
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        var departmentId = await connection.QuerySingleAsync<long>(
            "SELECT MIN(department_id) FROM departments WHERE is_active = 1 AND is_deleted = 0");
        var itemId = await connection.QuerySingleAsync<long>(
            "SELECT MIN(item_id) FROM items WHERE is_deleted = 0");
        return (departmentId, itemId);
    }

    private static async Task<RequisitionDatabaseRow> GetRequisitionAsync(long id)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        return await connection.QuerySingleAsync<RequisitionDatabaseRow>(
            "SELECT status AS \"Status\", rejection_reason AS \"RejectionReason\", row_version AS \"RowVersion\" FROM requisitions WHERE requisition_id = :id",
            new { id });
    }

    private static async Task<int> CountRequisitionsAsync()
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        return await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM requisitions");
    }

    private static async Task DeleteRequisitionsAsync(IReadOnlyCollection<long> ids)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.ExecuteAsync("DELETE FROM requisition_lines WHERE requisition_id IN :ids", new { ids });
        await connection.ExecuteAsync("DELETE FROM requisitions WHERE requisition_id IN :ids", new { ids });
    }

    private static string ExtractToken(string html)
    {
        var match = AntiforgeryTokenRegex().Match(html);
        Assert.True(match.Success, "頁面必須包含 AntiForgery request token。");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static long ExtractId(Uri? location)
    {
        Assert.NotNull(location);
        return long.Parse(location.OriginalString.Split('/').Last(), CultureInfo.InvariantCulture);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryTokenRegex();

    [GeneratedRegex("name=\"rowVersion\" value=\"([0-9]+)\"")]
    private static partial Regex RowVersionRegex();

    private sealed record RequisitionDatabaseRow(string Status, string? RejectionReason, decimal RowVersion);

    public sealed class RequisitionWebApplicationFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<MedSupplyOpsDbContext>>();
                services.RemoveAll<MedSupplyOpsDbContext>();
                services.RemoveAll<InventoryQueries>();
                services.AddDbContext<MedSupplyOpsDbContext>(options => options.UseOracle(OracleTestDatabase.ConnectionString));
                services.AddScoped<InventoryQueries>(serviceProvider =>
                    new InventoryQueries(serviceProvider.GetRequiredService<MedSupplyOpsDbContext>().Database.GetDbConnection()));
            });
        }
    }
}
