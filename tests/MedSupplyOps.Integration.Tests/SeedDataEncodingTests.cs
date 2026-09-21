using Dapper;
using Oracle.ManagedDataAccess.Client;

namespace MedSupplyOps.Integration.Tests;

/// <summary>
/// ★ 種子資料的字元編碼完整性。
///
/// 這個檔案存在的原因（血淚）：
/// 種子資料的中文曾經**全部是 U+FFFD 替代字元**（`EF BF BD`）——
/// 資料庫裡存的不是「無菌檢查手套」，是 6 個 `�`。
///
/// 根因是容器的初始化腳本呼叫 sqlplus 時沒有設 <c>NLS_LANG=.AL32UTF8</c>，
/// sqlplus 用 OS 的預設字元集去解讀 UTF-8 的 <c>.sql</c> 檔，
/// 在**寫進資料庫之前**就把每個中文字換成了替代字元。
/// 資料庫本身是 AL32UTF8，於是它忠實地存下那些替代字元，**不會報任何錯**。
///
/// 為什麼當時所有檢查都沒抓到：
/// 1. DDL 與 ASCII 資料（料號 MD-0001）完全正常，腳本回報成功。
/// 2. .NET 的整合測試「寫入 20 個中文字再讀回來比對」會過 ——
///    因為那是**同一條編碼路徑的 round-trip**，寫錯讀錯也會一致。
/// 3. 查詢測試斷言的是**料號**（純 ASCII），不是品名。
/// 4. 驗收時要求「貼出你從資料庫讀回來的字串」，
///    但主控台的編碼會把正常字串也顯示成亂碼 —— **肉眼看 SELECT 結果是不可信的觀測管道**。
///
/// 所以這裡的斷言刻意不比對「字串長什麼樣」，而是比對**位元組層級的事實**：
/// 有沒有替代字元、以及字元數與位元組數的比例對不對。
/// </summary>
public sealed class SeedDataEncodingTests
{
    /// <summary>U+FFFD REPLACEMENT CHARACTER。編碼轉換失敗時的產物。</summary>
    private const char ReplacementChar = '�';
    private const string StockLotsComment = "庫存批次。品項+批號+儲位 的唯一組合（效期不在唯一鍵內：同一批號只有一個效期，入庫時效期不同會被拒絕）；數量掛在這裡。";

    private static async Task<OracleConnection> OpenAsync()
    {
        var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    [Fact]
    public async Task No_seeded_text_contains_the_unicode_replacement_character()
    {
        await using var connection = await OpenAsync();

        // 直接問資料庫「有幾筆含替代字元」，而不是把字串撈回 .NET 再看 ——
        // 撈回來的路徑本身也可能出問題，用資料庫自己的函式判斷少一層可疑環節。
        var offenders = await connection.QueryAsync<string>(
            """
            SELECT item_code || ':item_name' FROM items
             WHERE INSTR(item_name, UNISTR('\FFFD')) > 0
            UNION ALL
            SELECT item_code || ':specification' FROM items
             WHERE INSTR(NVL(specification, 'x'), UNISTR('\FFFD')) > 0
            UNION ALL
            SELECT department_code || ':department_name' FROM departments
             WHERE INSTR(department_name, UNISTR('\FFFD')) > 0
            UNION ALL
            SELECT lot_number || ':storage_location' FROM stock_lots
             WHERE INSTR(storage_location, UNISTR('\FFFD')) > 0
            UNION ALL
            SELECT location_code || ':name' FROM storage_locations
             WHERE INSTR(name, UNISTR('\FFFD')) > 0
            """);

        var list = offenders.ToList();
        Assert.True(
            list.Count == 0,
            $"以下欄位含 U+FFFD 替代字元，代表資料在寫入前就被編碼轉換破壞了：{string.Join(", ", list)}");
    }

    [Fact]
    public async Task Seeded_chinese_names_have_a_multi_byte_footprint()
    {
        await using var connection = await OpenAsync();

        // AL32UTF8 下中文字佔 3 個位元組。若品名全是 ASCII（或被換成單位元組的 '?'），
        // LENGTHB 會等於 LENGTH —— 那就代表中文沒有真的存進去。
        var rows = (await connection.QueryAsync<(string Code, decimal Chars, decimal Bytes)>(
            """
            SELECT item_code AS Code, LENGTH(item_name) AS Chars, LENGTHB(item_name) AS Bytes
            FROM items
            WHERE created_by = 'seed'
            ORDER BY item_code
            """)).ToList();

        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.True(
            row.Bytes > row.Chars,
            $"{row.Code} 的品名 LENGTH={row.Chars}、LENGTHB={row.Bytes}，兩者相等代表裡面沒有中文。"));
    }

    [Fact]
    public async Task At_least_one_seeded_item_name_is_long_enough_to_prove_char_semantics()
    {
        await using var connection = await OpenAsync();

        // schema 的欄位是 VARCHAR2(200 CHAR)。若誤用 BYTE 語意，
        // 一個 16 字的中文品名（48 bytes）仍然塞得進 VARCHAR2(200 BYTE)，
        // 所以這條測不出 BYTE/CHAR 的差別 —— 它測的是「種子資料裡真的有長中文名」，
        // 讓 BYTE 語意的問題在資料量長大時有機會提早現形。
        var longest = await connection.ExecuteScalarAsync<decimal>(
            "SELECT MAX(LENGTH(item_name)) FROM items WHERE created_by = 'seed'");

        Assert.True(longest >= 15, $"最長的品項名稱只有 {longest} 個字，不足以驗證中文長度處理。");
    }
    /// <summary>
    /// ★ 資料字典裡的中文註解也必須完好。
    ///
    /// 這條測試是補一道縫：當時修好了成因（初始化腳本補上 NLS_LANG），
    /// 也補了上面那幾條檢查 —— 但它們檢查的是**種子資料的列**。
    /// 已經被寫壞的 <c>V001</c> 表／欄位註解沒有人回頭修，也沒有任何關卡照得到它們
    /// （ER 圖產生器不讀註解、App 畫面看不到註解），
    /// 於是它們從 2026-08-23 一直壞到 2026-09-10，
    /// 最後是備份還原演練跑 Data Pump 回報 ORA-39346 才被照出來。
    ///
    /// **修好成因，不等於修好既有的損壞。** 兩件事都要做，而且都要有關卡看著。
    /// 修復本身在 <c>db/schema/V005__repair_v001_comments.sql</c>。
    /// </summary>
    [Fact]
    public async Task No_data_dictionary_comment_contains_the_unicode_replacement_character()
    {
        await using var connection = await OpenAsync();

        var offenders = await connection.QueryAsync<string>(
            """
            SELECT 'TABLE  ' || table_name FROM user_tab_comments
            WHERE comments IS NOT NULL AND INSTR(comments, UNISTR('\FFFD')) > 0
            UNION ALL
            SELECT 'COLUMN ' || table_name || '.' || column_name FROM user_col_comments
            WHERE comments IS NOT NULL AND INSTR(comments, UNISTR('\FFFD')) > 0
            """);

        var list = offenders.ToList();
        Assert.True(
            list.Count == 0,
            $"資料字典有 {list.Count} 個註解含 U+FFFD 替代字元：{string.Join(", ", list)}。" +
            "這代表某支遷移是在 NLS_LANG 未設定的情況下被套用的。");
    }

    [Fact]
    public async Task Stock_lots_comment_matches_the_correct_chinese_text_exactly()
    {
        await using var connection = await OpenAsync();

        var comment = await connection.QuerySingleAsync<string>(
            "SELECT comments FROM user_tab_comments WHERE table_name = 'STOCK_LOTS'");

        Assert.Equal(StockLotsComment, comment);
    }

    /// <summary>
    /// 註解不只要「沒有替代字元」，還得**真的存在**。
    /// 若哪天有人把註解整批刪掉，上面那條測試會照樣全綠 —— 沒有註解就沒有壞註解。
    /// </summary>
    [Fact]
    public async Task Domain_tables_still_carry_their_chinese_comments()
    {
        await using var connection = await OpenAsync();

        var documented = await connection.QuerySingleAsync<int>(
            """
            SELECT COUNT(*) FROM user_tab_comments
            WHERE comments IS NOT NULL
              AND table_name IN ('ITEMS', 'STOCK_LOTS', 'STORAGE_LOCATIONS', 'REQUISITION_LINES', 'ISSUE_ALLOCATIONS', 'AUDIT_LOGS')
              AND LENGTHB(comments) > LENGTH(comments)
            """);

        // LENGTHB > LENGTH 代表內容確實含多位元組字元（中文），不是被換成 ASCII 佔位字串。
        Assert.Equal(6, documented);
    }
}
