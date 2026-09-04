using System.Net;
using System.Text.RegularExpressions;

namespace MedSupplyOps.Integration.Tests.Web;

/// <summary>
/// 讓整合測試用真正的 <c>/Account/Login</c> 端點登入示範帳號，而不是繞過驗證直接塞 Cookie ——
/// 走真實的登入路徑，才能同時驗證 D4（有登入者才寫得進 created_by）與 Identity 本身接得起來。
/// </summary>
internal static partial class WebAuthTestHelpers
{
    public const string DemoPassword = "Demo#2026pass";

    public static async Task LoginAsync(HttpClient client, string email, string password = DemoPassword)
    {
        var loginPage = await client.GetAsync("/Account/Login");
        var html = await loginPage.Content.ReadAsStringAsync();
        var token = ExtractToken(html);

        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["Email"] = email,
                ["Password"] = password,
            }));

        if (response.StatusCode != HttpStatusCode.Redirect)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"登入 {email} 失敗：HTTP {(int)response.StatusCode}。回應內容：{body}");
        }
    }

    private static string ExtractToken(string html)
    {
        var match = TokenRegex().Match(html);
        if (!match.Success)
        {
            throw new InvalidOperationException("登入頁沒有 AntiForgery request token。");
        }

        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenRegex();
}
