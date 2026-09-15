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

    public static readonly IReadOnlyList<string> SupportedIds =
    [
        DefaultId,
        "UTC",
        "Asia/Tokyo",
        "Asia/Shanghai",
        "Asia/Singapore",
        "Asia/Seoul",
        "Europe/London",
        "America/New_York",
        "America/Los_Angeles",
    ];

    /// <summary>選單用的語系無關前綴（固定偏移的時區才標，避免夏令時間讓標示變成謊言）。</summary>
    public static string OffsetPrefix(string id) => id switch
    {
        DefaultId => "UTC+08:00 ",
        "Asia/Tokyo" => "UTC+09:00 ",
        "Asia/Shanghai" => "UTC+08:00 ",
        "Asia/Singapore" => "UTC+08:00 ",
        "Asia/Seoul" => "UTC+09:00 ",
        _ => string.Empty,
    };

    /// <summary>選單用的城市名稱，key 即為 @L[] 查表用的中文原文。</summary>
    public static string CityKey(string id) => id switch
    {
        DefaultId => "台北",
        "UTC" => "UTC",
        "Asia/Tokyo" => "東京",
        "Asia/Shanghai" => "上海",
        "Asia/Singapore" => "新加坡",
        "Asia/Seoul" => "首爾",
        "Europe/London" => "倫敦",
        "America/New_York" => "紐約",
        "America/Los_Angeles" => "洛杉磯",
        _ => id,
    };

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
            return value is not null && SupportedIds.Contains(value, StringComparer.Ordinal)
                ? value
                : DefaultId;
        }
    }

    public DateTime ConvertFromUtc(DateTime utc)
    {
        var normalizedUtc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(normalizedUtc, TimeZoneInfo.FindSystemTimeZoneById(Id));
    }

    public string Format(DateTime utc, string format = "yyyy-MM-dd HH:mm:ss") =>
        ConvertFromUtc(utc).ToString(format, CultureInfo.InvariantCulture);
}
