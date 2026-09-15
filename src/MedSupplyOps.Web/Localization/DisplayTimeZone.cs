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

    public static readonly IReadOnlyList<string> SupportedIds = [DefaultId, "UTC"];

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
