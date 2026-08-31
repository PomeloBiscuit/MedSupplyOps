# Dapper FEFO 查詢與 Oracle 索引量測

量測日期：2026-08-31。資料庫為本機 `localhost:1521/FREEPDB1` 的 Oracle Database Free；沒有對外連線。

讀取路徑刻意使用 Dapper 與手寫 Oracle SQL：這讓讀取端的篩選、排序、綁定參數，以及索引是否真正被使用都可被直接檢視和調校；EF Core 則繼續只負責既有的寫入對映。

完整、未摘錄的 `DBMS_XPLAN.DISPLAY_CURSOR(..., 'ALLSTATS LAST')` 輸出在 [dapper-fefo-plan-output.txt](dapper-fefo-plan-output.txt)。該檔是量測腳本的原始輸出，不是手抄的預估計畫。

## 量測方法與資料量

`db/perf/01_seed_stock_lots.sql` 在既有的 `MD-0001` 品項上加入 100,000 筆標記為 `created_by = 'perf-fefo'` 的批次：99,900 筆在 `2026-01-01` 到期，100 筆在 `2027-06-01` 到期。量測基準日是 `2027-01-01`，因此索引篩選的候選集為 100 筆；加上 V002 原有的 `GLO-FEFO-D`，兩種計畫都回傳 101 筆。

量測前 `stock_lots` 實際筆數為 **100,011**；量測後由 `db/perf/03_cleanup_stock_lots.sql` 只刪除 `perf-fefo` 標記，實際輸出為：

```text
100000 rows deleted.

STOCK_LOTS_COUNT
----------------
              11
```

所以 V002 的 11 筆示範批次沒有被污染。

## T1 — 排除快取造成的假改善

門檻採 `ALLSTATS LAST` 的 **Buffers**（Oracle logical reads），不採用執行時間。資料是否已在 buffer cache 只會影響 physical I/O 與 elapsed time；每次存取 buffer 的邏輯讀取計數仍會被計入。因此先跑的全掃描與後跑的索引掃描即使後者是暖快取，**1,004 → 111 Buffers** 仍然是讀取工作量少了 893 次（88.9%），不是把「磁碟變記憶體」誤當成索引改善。

兩次量測使用相同的 100,011 筆資料、相同 `:itemId` / `:asOf` 值、同一份已收集統計資料，且 A-Rows 都是 **101**。前者將既有 `ix_stock_lots_fefo` 設為 `INVISIBLE` 並明確以 `FULL(l)` 重現「沒有 FEFO 索引」的基準路徑；後者立即恢復 `VISIBLE` 後不加索引提示。這不是拿兩次時間相減。

## T2 — 實際執行計畫與關鍵數字

量測 SQL 與應用程式相同的篩選／排序形狀；SQL*Plus 的日期 bind 以 `TO_DATE(:asOf, 'YYYY-MM-DD')` 表達，避免將日期字面值拼進 SQL。兩份完整計畫原文在上述 `.txt`，其實際節點如下。

### 索引不可見時（基準）

```text
Plan hash value: 105805309

| Id  | Operation            | Name       | Starts | E-Rows | A-Rows | A-Time       | Buffers |
|   0 | SELECT STATEMENT     |            |      1 |        |    101 | 00:00:00.01 |    1004 |
|   1 |  SORT ORDER BY       |            |      1 |    102 |    101 | 00:00:00.01 |    1004 |
|*  2 |   TABLE ACCESS FULL  | STOCK_LOTS |      1 |    102 |    101 | 00:00:00.01 |    1004 |
```

### 索引可見時（調校後）

```text
Plan hash value: 2273857294

| Id  | Operation                   | Name                | Starts | E-Rows | A-Rows | A-Time       | Buffers |
|   0 | SELECT STATEMENT            |                     |      1 |        |    101 | 00:00:00.01 |     111 |
|*  1 |  TABLE ACCESS BY INDEX ROWID| STOCK_LOTS          |      1 |    102 |    101 | 00:00:00.01 |     111 |
|*  2 |   INDEX RANGE SCAN          | IX_STOCK_LOTS_FEFO |      1 |    102 |    101 | 00:00:00.01 |      10 |
```

改變的是第 2 行：`TABLE ACCESS FULL STOCK_LOTS` 變成 `INDEX RANGE SCAN IX_STOCK_LOTS_FEFO`，且排序節點消失。總 Buffers 從 **1,004** 降至 **111**；A-Rows 兩邊都是 **101**，所以沒有以改變查詢語意換取數字。`IX_STOCK_LOTS_FEFO` 的索引節點本身只用了 **10** Buffers，其餘是 101 筆資料列的 rowid 回表與上層工作。

## T3 — 為何不是只量 11 筆

11 筆資料不足以讓全表掃描與索引範圍掃描的差異有意義，所以量測加入 100,000 筆刻意設計的壓測資料。查詢仍只取回 101 筆，卻必須在無索引路徑檢查 100,011 筆；這讓掃描機制的差異可從 Buffers 與 A-Rows 同時驗證。壓測資料不放在 `db/schema`，也不會在容器啟動時自動套用。

## T4 — 綁定變數與 shared pool

應用程式的實際程式碼（`InventoryQueries.GetItemAvailabilityAsync`）將呼叫端的 `DateOnly` 明確轉成 Oracle `DATE` 對應的午夜 `DateTime`，並把值放入 Dapper 參數物件：

```csharp
const string sql = """
    ... WHEN expiry_date >= :asOf AND quantity > 0 THEN quantity ...
    FROM stock_lots
    WHERE item_id = :itemId
    ORDER BY expiry_date, lot_number, stock_lot_id
    """;

var asOfDate = asOf.ToDateTime(TimeOnly.MinValue);
var command = new CommandDefinition(sql, new { itemId, asOf = asOfDate }, transaction);
```

SQL 字串沒有把 `itemId` 或日期串接進去。相同 SQL 被呼叫 1,000 次、值各不相同時，Oracle shared pool 正常會重用 **一份 parent cursor／執行計畫**；只有資料型別或 optimizer 環境不相容等特殊狀況才會產生額外 child cursor，並不是每個值一份計畫。

## T5 — 效期當天的邊界

整合測試 `GetItemAvailabilityAsync_treats_a_lot_expiring_on_asOf_as_available` 建立一筆 `ExpiryDate == asOf`、數量 7 的批次，並斷言：

```csharp
Assert.Equal(7, result.AvailableQuantity);
var lot = Assert.Single(result.Lots);
Assert.Equal(Today, lot.ExpiryDate);
Assert.True(lot.IsAvailable);
```

查詢條件是 `expiry_date >= :asOf`，等價於「只有 `expiry_date < asOf` 才過期」。實際 mutation 將兩個可用量判定故意從 `>=` 改成 `>`（等價於把過期規則從 `<` 改成 `<=`）：grep 確認命中第 39、43 行後，該測試輸出 `Expected: 7`、`Actual: 0` 而失敗；之後已還原為正確條件。
