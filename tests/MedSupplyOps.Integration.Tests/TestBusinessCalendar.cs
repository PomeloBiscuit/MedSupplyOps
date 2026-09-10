using MedSupplyOps.Infrastructure.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace MedSupplyOps.Integration.Tests;

internal static class TestBusinessCalendar
{
    private static readonly FakeTimeProvider TimeProvider = new(new DateTimeOffset(2026, 9, 10, 19, 0, 0, TimeSpan.Zero));
    private static readonly BusinessCalendar Calendar = new(
        TimeProvider,
        TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei"));

    public static DateOnly Today => Calendar.Today;

    public static DateOnly SystemToday => new BusinessCalendar(
        global::System.TimeProvider.System,
        TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei")).Today;

    public static void ReplaceHostClock(IServiceCollection services)
    {
        var provider = new FakeTimeProvider(new DateTimeOffset(2026, 9, 10, 19, 0, 0, TimeSpan.Zero));
        var calendar = new BusinessCalendar(provider, TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei"));
        services.RemoveAll<TimeProvider>();
        services.RemoveAll<BusinessCalendar>();
        services.AddSingleton<TimeProvider>(provider);
        services.AddSingleton(calendar);
    }
}
