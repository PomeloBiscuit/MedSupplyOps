using System.Globalization;

namespace MedSupplyOps.Infrastructure.Localization;

/// <summary>
/// 原文／英文主檔欄位的唯一語言選擇規則。英文文化只在英文值非空時採用英文，
/// 否則靜默回退原文；選單則把另一種語言附在括號中供雙語核對。
/// </summary>
public static class BilingualText
{
    public static string? Resolve(string? original, string? english, CultureInfo? culture = null)
    {
        var effectiveCulture = culture ?? CultureInfo.CurrentUICulture;
        return effectiveCulture.TwoLetterISOLanguageName.Equals("en", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(english)
                ? english
                : original;
    }

    public static string Option(string original, string? english, CultureInfo? culture = null)
    {
        var primary = Resolve(original, english, culture) ?? original;
        if (string.IsNullOrWhiteSpace(english) || string.Equals(original, english, StringComparison.Ordinal))
        {
            return primary;
        }

        var secondary = string.Equals(primary, english, StringComparison.Ordinal) ? original : english;
        return $"{primary}（{secondary}）";
    }
}
