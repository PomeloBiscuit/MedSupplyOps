using Dapper;
using Oracle.ManagedDataAccess.Client;

namespace MedSupplyOps.Integration.Tests.Web;

/// <summary>
/// 請領人帳號的科室必須是指名的種子科室，不能是「代碼排第一的科室」。
///
/// ★ 這支測試存在的原因（L-027）：
/// 帳號種子原本用 <c>OrderBy(Code).First()</c> 挑科室，而且**每次主機啟動都會重綁**。
/// 資料庫是共用的，整合測試會暫時建立代碼像 <c>D1EE0F72409</c> 的科室 —— 它排在 <c>DEP-ER</c> 前面。
/// 只要任何主機（包括測試主機）剛好在那段時間啟動，示範帳號 <c>requester@example.local</c>
/// 就被綁到測試科室上：登入的人看到一個十六進位代碼的科室，
/// 而測試結束時那個科室因為外鍵刪不掉，變成殘留。
///
/// 抓到它的不是測試，是殘留檢查（關卡 6）。
/// </summary>
public sealed class DemoAccountSeedingTests
{
    /// <summary>README 上示範請領人所屬的科室（V002 種子資料的急診）。</summary>
    private const string SeedRequesterDepartmentCode = "DEP-ER";

    /// <summary>
    /// ★ 刻意不是 <c>"itest"</c>。
    /// 舊的測試夾具用 <c>CreatedBy != "itest"</c> 把測試科室擋在外面 ——
    /// 那個條件只認得一個字串，換一個前綴就擋不住。這支測試要驗的是規則本身，不是那個字串。
    /// 仍以 itest 開頭，萬一清理失敗，殘留檢查一樣抓得到。
    /// </summary>
    private const string TestActor = "itest-seed-order";

    [Fact]
    public async Task Requester_accounts_stay_on_the_named_seed_department_when_a_lower_sorting_department_exists()
    {
        var decoyCode = "A" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();

        // 誘餌：代碼以 A 開頭，排序一定在所有種子科室（DEP-*）前面。
        await connection.ExecuteAsync(
            "INSERT INTO departments (department_code, department_name, created_by) VALUES (:code, :name, :actor)",
            new { code = decoyCode, name = "排序誘餌科室 " + decoyCode, actor = TestActor });
        var decoyId = (long)await connection.ExecuteScalarAsync<decimal>(
            "SELECT department_id FROM departments WHERE department_code = :code",
            new { code = decoyCode });

        try
        {
            // 誘餌存在的期間啟動一個全新的主機 ——
            // Program.cs 的示範帳號種子與測試夾具的帳號種子都在這時執行。
            await using (var factory = new ApplicationStartupSmokeTests.ProductionLikeFactory())
            {
                _ = factory.CreateClient();
            }

            var bindings = new List<(string Email, string? DepartmentCode)>();
            foreach (var email in new[] { "requester@example.local", TestIdentitySeeder.RequesterEmail })
            {
                bindings.Add((email, await DepartmentCodeOfAsync(connection, email)));
            }

            // Assert.All 會列出每一個不符合的帳號，而不是停在第一個。
            Assert.All(bindings, binding => Assert.True(
                binding.DepartmentCode == SeedRequesterDepartmentCode,
                $"{binding.Email} 被綁到科室 {binding.DepartmentCode ?? "(沒有科室)"}，應該是 {SeedRequesterDepartmentCode}。"));
        }
        finally
        {
            // 就算產品退化、帳號真的被綁走，也要把資料庫還原 ——
            // 否則外鍵擋住刪除，殘留會連累之後的每一輪。
            await connection.ExecuteAsync(
                """
                UPDATE identity_users
                SET department_id = (
                    SELECT department_id FROM departments
                    WHERE department_code = :seedCode AND is_deleted = 0)
                WHERE department_id = :decoyId
                """,
                new { seedCode = SeedRequesterDepartmentCode, decoyId });
            await connection.ExecuteAsync(
                "DELETE FROM departments WHERE department_id = :decoyId",
                new { decoyId });
        }
    }

    private static Task<string?> DepartmentCodeOfAsync(OracleConnection connection, string email)
        => connection.QuerySingleOrDefaultAsync<string?>(
            """
            SELECT d.department_code
            FROM identity_users u
            LEFT JOIN departments d ON d.department_id = u.department_id
            WHERE u.normalized_email = :email
            """,
            new { email = email.ToUpperInvariant() });
}
