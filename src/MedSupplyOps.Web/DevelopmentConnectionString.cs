using System.Globalization;

namespace MedSupplyOps.Web;

/// <summary>
/// 開發環境的連線字串來源：repo 根目錄的 <c>.env</c>。
///
/// 為什麼要有這個而不是要求開發者設 User Secrets：
/// <c>docker compose</c> 用 <c>.env</c> 的 <c>APP_DB_PASSWORD</c> 建立資料庫帳號。
/// 若 App 另外要求再設一次 User Secrets，同一個密碼就有**兩份來源** ——
/// 而兩份不一致的症狀是「容器起得來、App 連不上」，看起來像網路或防火牆問題，
/// 排查方向會完全錯誤。共用同一份 <c>.env</c> 讓那種漂移不可能發生。
///
/// ⚠ 只在 Development 使用。正式環境一律走 <c>ConnectionStrings__MedSupplyOps</c> 環境變數。
/// </summary>
internal static class DevelopmentConnectionString
{
    private const string PasswordKey = "APP_DB_PASSWORD=";
    private const string LocalDataSource = "//localhost:1521/FREEPDB1";
    private const string ApplicationUser = "medsupply";

    /// <summary>
    /// 從 <paramref name="contentRootPath"/> 往上找 <c>.env</c>，取出 <c>APP_DB_PASSWORD</c>
    /// 並組成本機 Oracle 容器的連線字串。找不到就回傳 <c>null</c>（由呼叫端決定怎麼報錯）。
    /// </summary>
    public static string? TryComposeFromDotEnv(string contentRootPath)
    {
        var password = TryReadPassword(contentRootPath);
        if (string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"User Id={ApplicationUser};Password={password};Data Source={LocalDataSource}");
    }

    private static string? TryReadPassword(string contentRootPath)
    {
        // 從 Web 專案目錄往上找，最多五層就到 repo 根目錄了。
        // 用「往上找」而不是寫死相對路徑，因為 dotnet run 與 dotnet test
        // 的工作目錄不同 —— 寫死的話會有一邊找不到，而症狀是「另一邊莫名其妙不能跑」。
        var directory = new DirectoryInfo(contentRootPath);

        for (var depth = 0; depth < 5 && directory is not null; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, ".env");
            if (!File.Exists(candidate))
            {
                continue;
            }

            foreach (var line in File.ReadLines(candidate))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith(PasswordKey, StringComparison.Ordinal))
                {
                    return trimmed[PasswordKey.Length..].Trim();
                }
            }
        }

        return null;
    }
}
