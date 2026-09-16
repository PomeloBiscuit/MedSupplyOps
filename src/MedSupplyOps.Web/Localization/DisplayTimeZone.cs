using System.Globalization;

namespace MedSupplyOps.Web.Localization;

/// <summary>
/// 只負責把 UTC 時間轉成目前使用者的顯示時區。業務日期、FEFO 與單號日期仍由
/// BusinessCalendar 決定，刻意不讓這個偏好流進任何業務規則。
/// </summary>
public sealed class DisplayTimeZone
{
    public const string CookieName = "mso-display-time-zone";
    public const string DefaultId = "Asia/Taipei";

    private static readonly Dictionary<string, TimeZoneInfo> TimeZonesById;

    public static readonly IReadOnlyList<DisplayTimeZoneOption> SupportedTimeZones;

    public static readonly IReadOnlyList<string> SupportedIds;

    static DisplayTimeZone()
    {
        var timeZones = new Dictionary<string, TimeZoneInfo>(StringComparer.Ordinal);
        foreach (var systemTimeZone in TimeZoneInfo.GetSystemTimeZones())
        {
            var id = IanaId(systemTimeZone.Id);
            if (id is not null)
            {
                timeZones.TryAdd(id, systemTimeZone);
            }
        }

        // Windows 會列舉 Windows ID，因此上面會先轉成跨平台的 IANA ID；
        // 這兩個值則明確保證預設值與 UTC 一定存在。
        timeZones[DefaultId] = TimeZoneInfo.FindSystemTimeZoneById(DefaultId);
        timeZones["UTC"] = TimeZoneInfo.Utc;

        TimeZonesById = timeZones;
        SupportedTimeZones = timeZones
            .Select(pair => new DisplayTimeZoneOption(pair.Key, Region(pair.Key), City(pair.Key)))
            .OrderBy(option => option.Region, StringComparer.Ordinal)
            .ThenBy(option => option.City, StringComparer.Ordinal)
            .ThenBy(option => option.Id, StringComparer.Ordinal)
            .ToArray();
        SupportedIds = SupportedTimeZones.Select(option => option.Id).ToArray();
    }

    /// <summary>
    /// 選單顯示的是該時區「現在」的 UTC 偏移；有夏令時間的城市半年後可能顯示不同值，
    /// 這是正確行為，不能改回固定對照表。
    /// </summary>
    public static string OffsetPrefix(string id)
    {
        if (!TimeZonesById.TryGetValue(id, out var timeZone))
        {
            return string.Empty;
        }

        var offset = timeZone.GetUtcOffset(DateTimeOffset.UtcNow);
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        var absolute = offset.Duration();
        return $"UTC{sign}{absolute.Hours:00}:{absolute.Minutes:00} ";
    }

    private readonly IHttpContextAccessor _httpContextAccessor;

    public DisplayTimeZone(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public string Id
    {
        get
        {
            var value = _httpContextAccessor.HttpContext?.Request.Cookies[CookieName];
            return value is not null && TimeZonesById.ContainsKey(value)
                ? value
                : DefaultId;
        }
    }

    public DateTime ConvertFromUtc(DateTime utc)
    {
        var normalizedUtc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(normalizedUtc, TimeZonesById[Id]);
    }

    public string Format(DateTime utc, string format = "yyyy-MM-dd HH:mm:ss") =>
        ConvertFromUtc(utc).ToString(format, CultureInfo.InvariantCulture);

    public static bool IsSupported(string id) => TimeZonesById.ContainsKey(id);

    private static string? IanaId(string systemId)
    {
        if (systemId == "UTC" || systemId.Contains('/'))
        {
            return systemId;
        }

        return TimeZoneInfo.TryConvertWindowsIdToIanaId(systemId, out var ianaId) ? ianaId : null;
    }

    private static string Region(string id)
    {
        var separator = id.IndexOf('/');
        return separator > 0 ? id[..separator] : "UTC";
    }

    private static string City(string id)
    {
        var separator = id.IndexOf('/');
        return (separator >= 0 ? id[(separator + 1)..] : id)
            .Replace('/', '·')
            .Replace('_', ' ');
    }
}

public sealed record DisplayTimeZoneOption(string Id, string Region, string City);
