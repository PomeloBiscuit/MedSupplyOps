namespace MedSupplyOps.Infrastructure.Time;

/// <summary>業務規則與人類可見時間使用的日曆。資料庫時間戳一律維持 UTC。</summary>
public sealed class BusinessCalendar
{
    private readonly TimeProvider _timeProvider;
    private readonly TimeZoneInfo _businessTimeZone;

    public BusinessCalendar(TimeProvider timeProvider, TimeZoneInfo businessTimeZone)
    {
        _timeProvider = timeProvider;
        _businessTimeZone = businessTimeZone;
    }

    public DateOnly Today => DateOnly.FromDateTime(ToBusinessTime(UtcNow));

    public DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    public DateTime ToBusinessTime(DateTime utc)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _businessTimeZone);

    /// <summary>
    /// 業務時區「本月」的半開區間 [起, 迄)，兩端都已轉成 UTC。
    /// 呼叫端應以 <c>occurred &gt;= FromUtc AND occurred &lt; ToUtc</c> 篩選，不可用 UTC 月份切。
    /// </summary>
    public (DateTime FromUtc, DateTime ToUtc) CurrentMonthRangeUtc()
    {
        var businessNow = ToBusinessTime(UtcNow);
        var monthStart = new DateTime(businessNow.Year, businessNow.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var nextMonthStart = monthStart.AddMonths(1);
        return (
            TimeZoneInfo.ConvertTimeToUtc(monthStart, _businessTimeZone),
            TimeZoneInfo.ConvertTimeToUtc(nextMonthStart, _businessTimeZone));
    }
}
