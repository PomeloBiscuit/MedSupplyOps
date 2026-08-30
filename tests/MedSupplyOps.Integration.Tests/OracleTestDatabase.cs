using System;
using System.IO;
using MedSupplyOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace MedSupplyOps.Integration.Tests;

/// <summary>
/// 解析整合測試要用的 Oracle 連線，並建立 <see cref="MedSupplyOpsDbContext"/>。
///
/// 連線來源（依序）：
///   1. 環境變數 MEDSUPPLYOPS_TEST_CONNECTION（CI 用，直接是完整連線字串）。
///   2. repo 根目錄的 .env 檔的 APP_DB_PASSWORD（本機開發用）。
///      .env 已被 gitignore，密碼不會進 repo；本類別也不把密碼寫進任何檔案或輸出。
///
/// 設計裁定 D6：測試自備資料、自行清除。這裡的每個 DbContext 都在呼叫端開的交易裡跑，
/// 測試結束一律 rollback，資料庫回到測試前的狀態（連續跑幾次都一樣）。
/// </summary>
internal static class OracleTestDatabase
{
    private static readonly string DataSource = "//localhost:1521/FREEPDB1";
    private static readonly string AppUser = "medsupply";

    public static string ConnectionString { get; } = ResolveConnectionString();

    public static MedSupplyOpsDbContext CreateContext(Action<string>? sqlSink = null)
    {
        var options = new DbContextOptionsBuilder<MedSupplyOpsDbContext>()
            .UseOracle(ConnectionString)
            .EnableSensitiveDataLogging();

        if (sqlSink is not null)
        {
            options = options.LogTo(
                sqlSink,
                new[] { DbLoggerCategory.Database.Command.Name },
                LogLevel.Information);
        }

        return new MedSupplyOpsDbContext(options.Options);
    }

    private static string ResolveConnectionString()
    {
        var fromEnv = Environment.GetEnvironmentVariable("MEDSUPPLYOPS_TEST_CONNECTION");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }

        var password = ReadPasswordFromDotEnv();
        return $"User Id={AppUser};Password=\"{password}\";Data Source={DataSource};";
    }

    private static string ReadPasswordFromDotEnv()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, ".env");
            if (File.Exists(candidate))
            {
                foreach (var raw in File.ReadAllLines(candidate))
                {
                    var line = raw.Trim();
                    if (line.StartsWith("APP_DB_PASSWORD=", StringComparison.Ordinal))
                    {
                        return line["APP_DB_PASSWORD=".Length..].Trim().Trim('"');
                    }
                }
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException(
            "找不到資料庫連線設定。請設定環境變數 MEDSUPPLYOPS_TEST_CONNECTION，" +
            "或確認 repo 根目錄的 .env 內有 APP_DB_PASSWORD，且 Oracle 容器已啟動（docker compose start）。");
    }
}
