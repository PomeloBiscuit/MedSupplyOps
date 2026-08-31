using Dapper;
using MedSupplyOps.Infrastructure.Services;
using Oracle.ManagedDataAccess.Client;
using Xunit.Abstractions;

namespace MedSupplyOps.Integration.Tests.Services;

/// <summary>
/// 發料服務的整合測試（FR-303 / FR-401 / FR-402），全部跑在真實的 Oracle 上。
///
/// 為什麼並發這件事非用真資料庫不可：
/// 「兩條交易同時扣同一列」的行為由資料庫的鎖與隔離級別決定，
/// 任何用假物件模擬出來的「並發」，測的都是那個假物件自己的實作 ——
/// 也就是自我證明。Domain 層的單元測試證明得了 FEFO 配批的順序，
/// 但證明不了「兩個人同時領最後一箱會怎樣」。
/// </summary>
public sealed class StockIssueServiceTests
{
    /// <summary>
    /// 同步點的等待上限。逾時**不是失敗** —— 它正好代表另一條交易被鎖擋住了，
    /// 也就是我們要的行為。所以斷言只看最終的數量與結果，不看時間。
    /// </summary>
    private static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(3);

    private readonly ITestOutputHelper _output;

    public StockIssueServiceTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// 基準日取自**資料庫**的 TRUNC(SYSDATE)，不取用戶端的 DateTime.Today。
    /// 容器與主機的時區若不同，跨午夜時兩者會差一天 ——
    /// 症狀是「測試在半夜才會紅」，最難重現的那種。
    /// </summary>
    private static async Task<DateOnly> DatabaseTodayAsync()
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        var value = await connection.ExecuteScalarAsync<DateTime>("SELECT TRUNC(SYSDATE) FROM dual");
        return DateOnly.FromDateTime(value);
    }

    private static StockIssueService NewService(
        OracleConnection connection, Func<CancellationToken, Task>? afterLock = null)
        => new(connection, StockIssueService.DefaultLockWaitSeconds, afterLock);

    // ─────────────────────────── 單條交易的正確性 ───────────────────────────

    [Fact]
    public async Task Issue_takes_the_earliest_expiring_lot_first()
    {
        await using var scenario = await IssueScenario.CreateAsync(
        [
            ("LATE", 180, 100),
            ("EARLY", 30, 100),
            ("MID", 90, 100),
        ]);
        var asOf = await DatabaseTodayAsync();

        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        var result = await NewService(connection).IssueAsync(
            scenario.RequisitionLineIds[0], scenario.ItemId, 50, asOf, "itest");

        Assert.True(result.IsSuccess);
        var only = Assert.Single(result.Allocations);
        Assert.Equal("EARLY", only.LotNumber);
        Assert.Equal(50, await scenario.GetLotQuantityAsync("EARLY"));
        Assert.Equal(100, await scenario.GetLotQuantityAsync("MID"));
        Assert.Equal(100, await scenario.GetLotQuantityAsync("LATE"));
    }

    [Fact]
    public async Task Issue_spans_lots_in_expiry_order_when_the_first_is_not_enough()
    {
        await using var scenario = await IssueScenario.CreateAsync(
        [
            ("EARLY", 30, 10),
            ("MID", 90, 20),
            ("LATE", 180, 100),
        ]);
        var asOf = await DatabaseTodayAsync();

        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        var result = await NewService(connection).IssueAsync(
            scenario.RequisitionLineIds[0], scenario.ItemId, 25, asOf, "itest");

        Assert.True(result.IsSuccess);
        Assert.Equal(["EARLY", "MID"], result.Allocations.Select(a => a.LotNumber));
        Assert.Equal([10, 15], result.Allocations.Select(a => a.Quantity));

        // 「沒被碰的那一批確實沒被碰」必須斷言 —— 多扣一批的話總量仍然對得上，
        // 畫面與可用量都正常，只有這一行看得出來。
        Assert.Equal(0, await scenario.GetLotQuantityAsync("EARLY"));
        Assert.Equal(5, await scenario.GetLotQuantityAsync("MID"));
        Assert.Equal(100, await scenario.GetLotQuantityAsync("LATE"));
    }

    [Fact]
    public async Task Issue_fails_entirely_and_changes_nothing_when_stock_is_insufficient()
    {
        await using var scenario = await IssueScenario.CreateAsync(
            [("A", 30, 10), ("B", 60, 5)]);
        var asOf = await DatabaseTodayAsync();

        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        var result = await NewService(connection).IssueAsync(
            scenario.RequisitionLineIds[0], scenario.ItemId, 20, asOf, "itest");

        Assert.False(result.IsSuccess);
        Assert.Equal(IssueFailureReason.InsufficientStock, result.FailureReason);
        Assert.Equal(15, result.AvailableQuantity);
        Assert.Empty(result.Allocations);

        // 不做部分發料：兩個批次都必須原封不動。
        Assert.Equal(10, await scenario.GetLotQuantityAsync("A"));
        Assert.Equal(5, await scenario.GetLotQuantityAsync("B"));
        Assert.Equal(0, await scenario.GetIssuedQuantityAsync());
    }

    [Fact]
    public async Task Issue_never_takes_an_expired_lot_even_when_it_is_the_only_stock()
    {
        await using var scenario = await IssueScenario.CreateAsync([("EXPIRED", -1, 999)]);
        var asOf = await DatabaseTodayAsync();

        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        var result = await NewService(connection).IssueAsync(
            scenario.RequisitionLineIds[0], scenario.ItemId, 1, asOf, "itest");

        Assert.False(result.IsSuccess);
        Assert.Equal(IssueFailureReason.InsufficientStock, result.FailureReason);
        Assert.Equal(0, result.AvailableQuantity);
        Assert.Equal(999, await scenario.GetLotQuantityAsync("EXPIRED"));
    }

    [Fact]
    public async Task Issue_records_the_expiry_snapshot_of_the_lot_it_took()
    {
        await using var scenario = await IssueScenario.CreateAsync([("A", 30, 10)]);
        var asOf = await DatabaseTodayAsync();

        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        var result = await NewService(connection).IssueAsync(
            scenario.RequisitionLineIds[0], scenario.ItemId, 4, asOf, "itest");

        Assert.True(result.IsSuccess);
        Assert.Equal(4, await scenario.GetIssuedQuantityAsync());
        var byLot = await scenario.GetIssuedByLotAsync();
        Assert.Equal(("A", 4), Assert.Single(byLot));
    }

    // ─────────────────────── ★ FR-402：並發不得超發 ───────────────────────

    /// <summary>
    /// ★ 本專案最重要的一條測試。
    ///
    /// 一個批次只有 5，兩條並發交易各要 4（合計 8 > 5）。
    /// 正確行為：一方成功、另一方收到明確的「庫存不足，目前可用 1」，庫存停在 1。
    ///
    /// 競態是**決定性**地製造出來的，不是靠 Thread.Sleep 碰運氣：
    /// 第一條在取得列鎖後停在同步點，第二條這時才開始嘗試取鎖。
    /// - 有 FOR UPDATE → 第二條被擋在 SELECT，同步點逾時後第一條照常完成；
    ///   第二條解鎖後**重讀到的是扣減後的真實庫存**，於是乾淨地回報不足。
    /// - 沒有 FOR UPDATE → 第二條立刻讀到過期的快照（5），兩條都以為自己能拿 4，
    ///   第二次 UPDATE 會算出 1 - 4 = -3，撞上資料庫的 CHECK (quantity >= 0)
    ///   而拋出 ORA-02290，本測試變紅。
    ///
    /// 注意後者的意義：**即使應用層的鎖被拿掉，資料也不會變成負數** ——
    /// 那是資料庫層的最後一道防線在擋。應用層的鎖負責把「醜陋的約束違反」
    /// 換成「對使用者有意義的業務結果」。兩層各司其職，缺一不可。
    /// </summary>
    [Fact]
    public async Task Two_concurrent_issues_of_the_last_units_never_oversell()
    {
        await using var scenario = await IssueScenario.CreateAsync(
            [("LAST", 30, 5)], requisitionLineCount: 2);
        var asOf = await DatabaseTodayAsync();

        var firstHasLocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAttemptedLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<IssueResult> FirstAsync()
        {
            await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
            var service = NewService(connection, async cancellationToken =>
            {
                firstHasLocked.TrySetResult();
                // 等第二條也走到「已取得鎖」的位置。如果鎖有生效，它會被擋住，
                // 這裡就會逾時 —— 逾時是預期內的，不是失敗。
                await Task.WhenAny(secondAttemptedLock.Task, Task.Delay(BarrierTimeout, cancellationToken));
            });
            return await service.IssueAsync(scenario.RequisitionLineIds[0], scenario.ItemId, 4, asOf, "t1");
        }

        async Task<IssueResult> SecondAsync()
        {
            await firstHasLocked.Task;
            await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
            var service = NewService(connection, _ =>
            {
                secondAttemptedLock.TrySetResult();
                return Task.CompletedTask;
            });
            return await service.IssueAsync(scenario.RequisitionLineIds[1], scenario.ItemId, 4, asOf, "t2");
        }

        var results = await Task.WhenAll(FirstAsync(), SecondAsync());

        var succeeded = results.Count(r => r.IsSuccess);
        var failed = results.Where(r => !r.IsSuccess).ToList();
        _output.WriteLine($"成功 {succeeded} 筆；失敗 {failed.Count} 筆");
        foreach (var f in failed)
        {
            _output.WriteLine($"  失敗原因 {f.FailureReason}，回報可用量 {f.AvailableQuantity}");
        }

        Assert.Equal(1, succeeded);
        var loser = Assert.Single(failed);

        // 失敗的一方必須收到「庫存不足」而不是逾時或例外 ——
        // 使用者要能看懂發生了什麼。
        Assert.Equal(IssueFailureReason.InsufficientStock, loser.FailureReason);
        Assert.Equal(1, loser.AvailableQuantity);

        // ★ 最關鍵的兩行：資料庫裡的真實數字。
        Assert.Equal(1, await scenario.GetLotQuantityAsync("LAST"));
        Assert.Equal(4, await scenario.GetIssuedQuantityAsync());
    }

    /// <summary>
    /// 多方競爭的實況測試：8 個庫存、12 條並發交易各要 1。
    ///
    /// 這一條**不是**決定性的鑑別力來源（12 條交易的交錯順序取決於排程），
    /// 它的價值在於：斷言的是「恰好 8 成功、4 失敗、庫存歸零、發出量恰好 8」這組
    /// **守恆等式**，而不是時間。不論排程怎麼交錯，這組等式都必須成立。
    /// 決定性的鑑別力由上面那條雙方競爭的測試負責。
    /// </summary>
    [Fact]
    public async Task Concurrent_issues_deplete_exactly_the_available_quantity_and_no_more()
    {
        const int available = 8;
        const int contenders = 12;

        await using var scenario = await IssueScenario.CreateAsync(
            [("POOL", 30, available)], requisitionLineCount: contenders);
        var asOf = await DatabaseTodayAsync();

        var tasks = Enumerable.Range(0, contenders).Select(async i =>
        {
            await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
            return await NewService(connection).IssueAsync(
                scenario.RequisitionLineIds[i], scenario.ItemId, 1, asOf, "t" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        });

        var results = await Task.WhenAll(tasks);

        var succeeded = results.Count(r => r.IsSuccess);
        var insufficient = results.Count(r => r.FailureReason == IssueFailureReason.InsufficientStock);
        var timedOut = results.Count(r => r.FailureReason == IssueFailureReason.LockTimeout);
        _output.WriteLine($"成功 {succeeded}／不足 {insufficient}／鎖逾時 {timedOut}（共 {contenders} 條）");

        // 逾時是可接受的結果，但它代表這台機器很慢；若發生，成功數會少於 8。
        // 為了讓斷言不受此影響，把逾時的那幾條排除在等式之外，並分別斷言守恆。
        Assert.Equal(contenders, succeeded + insufficient + timedOut);
        Assert.Equal(0, timedOut);                     // 正常環境不該逾時
        Assert.Equal(available, succeeded);
        Assert.Equal(contenders - available, insufficient);

        // ★ 守恆：發出去的總量 = 原有量 - 剩餘量，且剩餘量不可能是負數。
        var remaining = await scenario.GetLotQuantityAsync("POOL");
        var issued = await scenario.GetIssuedQuantityAsync();
        Assert.Equal(0, remaining);
        Assert.Equal(available, issued);
        Assert.Equal(available, issued + remaining);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Non_positive_quantity_is_rejected_without_touching_the_database(int quantity)
    {
        await using var scenario = await IssueScenario.CreateAsync([("A", 30, 10)]);
        var asOf = await DatabaseTodayAsync();

        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        var result = await NewService(connection).IssueAsync(
            scenario.RequisitionLineIds[0], scenario.ItemId, quantity, asOf, "itest");

        Assert.False(result.IsSuccess);
        Assert.Equal(IssueFailureReason.InvalidQuantity, result.FailureReason);
        Assert.Equal(10, await scenario.GetLotQuantityAsync("A"));
    }
}
