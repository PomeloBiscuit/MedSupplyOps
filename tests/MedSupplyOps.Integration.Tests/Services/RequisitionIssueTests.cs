using System.Globalization;
using Dapper;
using MedSupplyOps.Domain.Requisitions;
using MedSupplyOps.Infrastructure.Services;
using Oracle.ManagedDataAccess.Client;
using Xunit.Abstractions;

namespace MedSupplyOps.Integration.Tests.Services;

/// <summary>
/// 整張請領單發料（<see cref="StockIssueService.IssueRequisitionAsync"/>）的整合測試。
///
/// 本檔最重要的一條是 <see cref="A_single_insufficient_line_rolls_back_the_entire_requisition"/> ——
/// 它釘住的是「原子性」：一張單有三個品項，第三個庫存不足時，
/// **前兩個已經扣掉的必須全部退回**。
///
/// 為什麼這件事非測不可：若做錯，前兩個品項的庫存真的被扣走了，
/// 請領單卻停在 Approved（因為狀態轉換失敗）。
/// 畫面上顯示「發料失敗，庫存不足」—— 完全正確的訊息 ——
/// 而使用者不會知道倉庫裡已經少了兩箱東西。**要對帳才會發現。**
/// </summary>
public sealed class RequisitionIssueTests
{
    private readonly ITestOutputHelper _output;

    public RequisitionIssueTests(ITestOutputHelper output) => _output = output;

    private static async Task<DateOnly> DatabaseTodayAsync()
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        var value = await connection.ExecuteScalarAsync<DateTime>("SELECT TRUNC(SYSDATE) FROM dual");
        return DateOnly.FromDateTime(value);
    }

    [Fact]
    public async Task Issues_every_line_and_moves_the_requisition_to_issued()
    {
        await using var scenario = await RequisitionIssueScenario.CreateAsync(
        [
            new("A", [("A-EARLY", 30, 10), ("A-LATE", 180, 50)], RequestQuantity: 25),
            new("B", [("B-ONLY", 60, 40)], RequestQuantity: 5),
        ]);
        var asOf = await DatabaseTodayAsync();

        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        var result = await new StockIssueService(connection)
            .IssueRequisitionAsync(scenario.RequisitionId, asOf, "itest");

        Assert.True(result.IsSuccess, $"預期成功，實際：{result.FailureReason}");
        Assert.Equal(2, result.IssuedLines.Count);

        // FEFO：A 品項先扣光 A-EARLY(10) 再從 A-LATE 補 15。
        Assert.Equal(0, await scenario.GetLotQuantityAsync("A-EARLY"));
        Assert.Equal(35, await scenario.GetLotQuantityAsync("A-LATE"));
        Assert.Equal(35, await scenario.GetLotQuantityAsync("B-ONLY"));

        Assert.Equal(RequisitionStatus.Issued, await scenario.GetStatusAsync());
    }

    /// <summary>
    /// ★ 原子性：任一筆明細失敗，整張單都不發。
    /// </summary>
    [Fact]
    public async Task A_single_insufficient_line_rolls_back_the_entire_requisition()
    {
        // 前兩個品項庫存充足，第三個只有 1 個卻要領 99。
        await using var scenario = await RequisitionIssueScenario.CreateAsync(
        [
            new("A", [("A-OK", 30, 100)], RequestQuantity: 10),
            new("B", [("B-OK", 60, 100)], RequestQuantity: 20),
            new("C", [("C-SHORT", 90, 1)], RequestQuantity: 99),
        ]);
        var asOf = await DatabaseTodayAsync();

        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        var result = await new StockIssueService(connection)
            .IssueRequisitionAsync(scenario.RequisitionId, asOf, "itest");

        Assert.False(result.IsSuccess);
        Assert.Equal(RequisitionIssueFailureReason.InsufficientStock, result.FailureReason);

        // 必須指出是哪一個品項不夠 —— 一張單五個品項只回「庫存不足」對使用者沒有用。
        Assert.Equal(scenario.ItemIdOf("C"), result.FailedItemId);
        Assert.Equal(99, result.RequestedQuantity);
        Assert.Equal(1, result.AvailableQuantity);
        Assert.Empty(result.IssuedLines);

        // ★★ 這三行是本測試的重點：前兩個品項的庫存必須**原封不動**。
        Assert.Equal(100, await scenario.GetLotQuantityAsync("A-OK"));
        Assert.Equal(100, await scenario.GetLotQuantityAsync("B-OK"));
        Assert.Equal(1, await scenario.GetLotQuantityAsync("C-SHORT"));

        // 配批紀錄也不可以留下半筆。
        Assert.Equal(0, await scenario.GetIssuedQuantityAsync());

        // 狀態必須停在原地，不可以變成 Issued。
        Assert.Equal(RequisitionStatus.Approved, await scenario.GetStatusAsync());
    }

    [Fact]
    public async Task Rejects_issuing_a_requisition_that_has_not_been_approved()
    {
        await using var scenario = await RequisitionIssueScenario.CreateAsync(
            [new("A", [("A-OK", 30, 100)], RequestQuantity: 10)],
            status: RequisitionStatus.PendingApproval);
        var asOf = await DatabaseTodayAsync();

        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        var result = await new StockIssueService(connection)
            .IssueRequisitionAsync(scenario.RequisitionId, asOf, "itest");

        Assert.False(result.IsSuccess);
        Assert.Equal(RequisitionIssueFailureReason.IllegalStatusTransition, result.FailureReason);

        // 要告訴使用者「目前是什麼狀態」，否則他不知道該去做什麼。
        Assert.Equal(RequisitionStatus.PendingApproval, result.StatusAtFailure);

        Assert.Equal(100, await scenario.GetLotQuantityAsync("A-OK"));
        Assert.Equal(RequisitionStatus.PendingApproval, await scenario.GetStatusAsync());
    }

    /// <summary>
    /// 兩個人同時對同一張單按發料。
    /// 第二個必須被狀態機擋下（此時單子已是 Issued），而**不是把庫存扣兩次**。
    /// </summary>
    [Fact]
    public async Task Two_concurrent_issues_of_the_same_requisition_only_deduct_once()
    {
        await using var scenario = await RequisitionIssueScenario.CreateAsync(
            [new("A", [("A-POOL", 30, 100)], RequestQuantity: 30)]);
        var asOf = await DatabaseTodayAsync();

        async Task<RequisitionIssueResult> IssueAsync()
        {
            await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
            return await new StockIssueService(connection)
                .IssueRequisitionAsync(scenario.RequisitionId, asOf, "itest");
        }

        var results = await Task.WhenAll(IssueAsync(), IssueAsync());

        var succeeded = results.Count(r => r.IsSuccess);
        var rejected = results.Where(r => !r.IsSuccess).ToList();
        _output.WriteLine($"成功 {succeeded}／失敗 {rejected.Count}");
        foreach (var r in rejected)
        {
            _output.WriteLine($"  失敗原因 {r.FailureReason}，當下狀態 {r.StatusAtFailure}");
        }

        Assert.Equal(1, succeeded);
        var loser = Assert.Single(rejected);
        Assert.Equal(RequisitionIssueFailureReason.IllegalStatusTransition, loser.FailureReason);
        Assert.Equal(RequisitionStatus.Issued, loser.StatusAtFailure);

        // ★ 只扣一次：100 - 30 = 70，不是 40。
        Assert.Equal(70, await scenario.GetLotQuantityAsync("A-POOL"));
        Assert.Equal(30, await scenario.GetIssuedQuantityAsync());
    }

    /// <summary>
    /// 明細一律依 item_id 遞增順序處理（跨品項的死結防護）。
    /// 這裡不製造真的死結（那需要精確的交錯，不具決定性），
    /// 而是斷言**處理順序本身**：只要順序固定，兩張單就不可能互等。
    /// </summary>
    [Fact]
    public async Task Lines_are_processed_in_ascending_item_id_order()
    {
        // 建立順序刻意與 item_id 順序無關：先建 C 再建 A 再建 B。
        // 但 item_id 是遞增的 identity，所以建立順序 = id 順序。
        await using var scenario = await RequisitionIssueScenario.CreateAsync(
        [
            new("first", [("F", 30, 100)], RequestQuantity: 1),
            new("second", [("S", 30, 100)], RequestQuantity: 1),
            new("third", [("T", 30, 100)], RequestQuantity: 1),
        ]);
        var asOf = await DatabaseTodayAsync();

        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        var result = await new StockIssueService(connection)
            .IssueRequisitionAsync(scenario.RequisitionId, asOf, "itest");

        Assert.True(result.IsSuccess);

        var itemIds = result.IssuedLines.Select(l => l.ItemId).ToList();
        _output.WriteLine("處理順序：" + string.Join(", ", itemIds.Select(i => i.ToString(CultureInfo.InvariantCulture))));

        Assert.Equal(itemIds.OrderBy(id => id), itemIds);
    }
}
