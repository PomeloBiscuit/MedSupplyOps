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
}
