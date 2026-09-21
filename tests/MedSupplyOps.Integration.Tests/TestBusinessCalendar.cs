using MedSupplyOps.Infrastructure.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MedSupplyOps.Integration.Tests;

/// <summary>
/// 測試用的時間來源。
///
/// ★ 替換的是 <see cref="TimeProvider"/>，**不是** <see cref="BusinessCalendar"/>。
///
/// 第一版同時換掉兩者，還自己 new 一個日曆塞進 DI —— 那樣測試**永遠用不到產品的日曆註冊**。
/// 而產品的註冊當時確實是壞的：<c>Program.cs</c> 用寫死的 <c>TimeProvider.System</c> 建日曆，
/// 沒有從 DI 取時鐘，所以換掉 DI 裡的時鐘對產品毫無作用 —— 測試完全看不到，因為它連日曆一起換了。
/// 那等於測試自己提供了它要驗證的那個東西。
///
/// 現在只換時鐘，產品的日曆照它自己的方式從 DI 取時鐘。
/// 註冊壞了，產品的「今天」就不會跟著動，邊界測試就會紅。
/// </summary>
internal static class TestBusinessCalendar
{
    /// <summary>
    /// 預設測試時間：UTC 2026-09-10 19:00 = 台灣 2026-09-11 03:00 ——
    /// UTC 還停在前一天的那個邊界時段。
    /// </summary>
    public static readonly DateTimeOffset DefaultInstant = new(2026, 9, 10, 19, 0, 0, TimeSpan.Zero);

    private static readonly TimeZoneInfo BusinessTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");

    /// <summary>
    /// 所有替換了時鐘的測試主機共用這一個。
    /// 測試不平行執行（見 <c>AssemblyInfo.cs</c>），所以共用是安全的；
    /// 撥動它的測試必須在 finally 裡撥回 <see cref="DefaultInstant"/>。
    /// </summary>
    public static TestClock HostClock { get; } = new(DefaultInstant);

    /// <summary>測試主機目前的業務日期（跟著 <see cref="HostClock"/> 走）。</summary>
    public static DateOnly Today => new BusinessCalendar(HostClock, BusinessTimeZone).Today;

    /// <summary>
    /// 真實時鐘的業務日期。**查詢種子資料的測試要用這個**：
    /// 種子資料的效期是建庫當天以 <c>TRUNC(SYSDATE) ± n</c> 算的相對日期，
    /// 拿一個固定的假日期去比對它，下次在別的日子重新建庫就會錯開。
    /// </summary>
    public static DateOnly SystemToday => new BusinessCalendar(TimeProvider.System, BusinessTimeZone).Today;

    public static void ReplaceHostClock(IServiceCollection services)
    {
        services.RemoveAll<TimeProvider>();
        services.AddSingleton<TimeProvider>(HostClock);
    }
}

/// <summary>
/// 可以任意設定、也可以撥回去的時鐘。
///
/// 不用官方的 <c>FakeTimeProvider</c>：它的 <c>SetUtcNow</c> 不允許時間倒退
/// （實測丟 <c>ArgumentOutOfRangeException: Cannot go back in time</c>），
/// 而邊界測試需要把時間撥到遠方的日期、做完再撥回來。
/// </summary>
internal sealed class TestClock(DateTimeOffset initial) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = initial;

    public override DateTimeOffset GetUtcNow() => UtcNow;
}
