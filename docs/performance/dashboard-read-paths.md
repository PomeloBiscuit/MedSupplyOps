# 首頁儀表板與已過期仍在庫讀取路徑

量測日期：2026-09-14。資料庫為本機 `medsupplyops-oracle` 的 `MEDSUPPLY` schema；沒有對外連線。完整、未摘錄的四組 `DBMS_XPLAN.DISPLAY_CURSOR(..., 'ALLSTATS LAST')` 輸出在 [dashboard-read-paths-plan-output.txt](dashboard-read-paths-plan-output.txt)。

量測腳本是 `scripts/measure-dashboard-read-paths.ps1`，所有容器 SQL 都經過 `scripts/lib/ContainerExec.ps1` 的無 BOM 檔案 + `docker cp` 路徑；沒有使用 stdin 管線。效能資料由 `db/perf/06_seed_dashboard_read_paths.sql` 產生、`09_cleanup_dashboard_read_paths.sql` 清除，標記一律是 `itest-perf`，因此第六關會抓到任何中斷殘留。

## D1 — 資料量與查詢語意

本次實際造出：

| 資料 | 列數 | 配置 |
|---|---:|---|
| `audit_logs` | 200,000 | 最新 50,000 列共用 `occurred_at`，強制驗證 `audit_log_id DESC` 的第二排序鍵 |
| `stock_lots` | 20,000 | 100 列已過期、仍有數量（0.5%）；其餘未過期 |

受測 SQL 與 `DashboardQueries.GetRecentAuditEntriesAsync`、`GetExpiredInStockLotCountAsync` 的 SELECT、JOIN、篩選與 bind 完全相同；只加入 `GATHER_PLAN_STATISTICS` 與互斥的 `WO_S_*` 註解以保存四個 cursor。稽核 feed 的結果列數（根節點 A-Rows）前後都是 **5**；過期計數的聚合結果根節點 A-Rows 前後都是 **1**，其輸入資料列 A-Rows 前後都是 **107**（100 筆量測列加既有 7 筆種子列）。沒有縮小結果集或修改產品 SQL。

## T1、D2 — 每支查詢都雙跑

採用第二次的 `ALLSTATS LAST` Buffers 作為比較數字，兩次完整計畫都在原始輸出檔。實測如下：

| 讀取路徑 | Run 1 Buffers | Run 2 Buffers | Run 2 關鍵操作／A-Rows |
|---|---:|---:|---|
| 稽核 feed、V007 前 | 2,006 | 2,006 | `TABLE ACCESS FULL AUDIT_LOGS` 200K，`SORT ORDER BY STOPKEY` 5 |
| 稽核 feed、V007 後 | 54 | 54 | `INDEX FULL SCAN IX_AUDIT_LOGS_DASHBOARD_FEED` 5，根節點 5 |
| 已過期仍在庫、V007 前 | 19 | 19 | `INDEX RANGE SCAN IX_STOCK_LOTS_FEFO` 108，聚合根節點 1 |
| 已過期仍在庫、V007 後 | 19 | 19 | 同上 |

這裡必須校正原先假設裡「第一次 Buffers 會包含把區塊放進 buffer cache 的成本」的前提：那是 physical I/O／elapsed time 的特性，不是 Oracle `Buffers`（logical reads）的特性。本次兩次的 Buffers 全數相同，正是直接證據。雙跑仍保留，因為它能揭露 cursor／計畫不穩定；但不可把第一次與第二次的 Buffers 差額解讀成 cache 變暖。

## T2、D4 — 索引判斷

結論：**新增一支索引，但僅給首頁稽核 feed；已過期計數不新增索引。**

V007 的 `ix_audit_logs_dashboard_feed (occurred_at DESC, audit_log_id DESC)` 把稽核 feed 從掃描／JOIN 200K 列再排序，改成以完整排序鍵直接取 5 列；第二次 Buffers **2,006 → 54**，少 **1,952（97.3%）**，且 A-Rows 始終是 5。`schema_versions` 實際回報：

```text
VERSION  SCRIPT_NAME
V007     V007__add_audit_log_dashboard_feed_index.sql
```

V007 實際內容：

```sql
CREATE INDEX ix_audit_logs_dashboard_feed
    ON audit_logs (occurred_at DESC, audit_log_id DESC);
```

「已過期仍在庫」已由既有 `ix_stock_lots_fefo (item_id, expiry_date, lot_number, stock_lot_id)` 對每個未停用品項做 `INDEX RANGE SCAN`，在 20,000 新增批次、107 筆實際候選列下只需 19 Buffers。V007 前後完全一樣；額外索引只會增加每次入庫的寫入與統計維護成本，沒有可證的讀取收益，因此不加。

## T3 — 假改善的反證

若錯把第一次與第二次的 **elapsed time** 當成改善，V007 前稽核 feed 的 A-Time `00:00:00.09 → 00:00:00.07` 看起來是 **22.2%** 改善；這個數字不能採信，因為兩次查詢、排程、cache 狀態都不是兩種設計的對照。相同兩次的 Buffers 都是 **2,006**，所以以正確指標計算的「第一次比第二次改善」是 **0**。這正是本專案不用執行時間下結論的原因。

## T4 — 清除

`09_cleanup_dashboard_read_paths.sql` 的實際輸出為：

```text
200000 rows deleted.
20000 rows deleted.
PERF_AUDIT_LOG_ROWS_AFTER_CLEANUP  0
PERF_STOCK_LOT_ROWS_AFTER_CLEANUP  0
```

後續 `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/check-db-clean.ps1` 回報：

```text
資料庫乾淨：沒有 created_by / actor LIKE 'itest%' 的殘留資料。
```

## T5 — 跨程序測試互斥與探針防呆

整合測試組件以 `Global\MedSupplyOps.IntegrationTests` mutex 保護，並掛在 xUnit v2 adapter 實際呼叫的組件級 `ITestCaseOrderer`。第一個 `dotnet test` 實際通過 **110/110**；同時啟動的第二個程序在 mutex 5 秒後（命令總耗時約 8 秒）中止，沒有開始任何測試，實際訊息：

```text
使用中的測試回合已中止。原因: 測試主機處理序當機 :
MedSupplyOps integration tests refused to start: another testhost holds
Global\MedSupplyOps.IntegrationTests. This suite shares one Oracle schema.
Stop or wait for the other dotnet test process, then retry; this run intentionally
waits no longer than 5 seconds.
```

`mutation-probe.ps1` 在一個 FHIR 整合測試 `testhost` 執行時實際拒絕：

```text
偵測到 testhost 程序正在執行（PID: 34628）；拒絕開始鑑別力探針。
請等待或停止另一個 dotnet test 程序後再執行；本腳本不會自動終止別人的測試。
```

該程序結束後，探針恢復正常：領域基線 **48/48**、整合基線 **110/110**，**P1～P12 全部有鑑別力**；還原後兩組基線仍分別為 48/48、110/110。

## 六道關卡

| 關卡 | 實際結果 |
|---|---|
| `dotnet build MedSupplyOps.slnx --nologo` | 成功，0 warnings／0 errors |
| `dotnet test MedSupplyOps.slnx --nologo` | 領域 48/48；整合 110/110 |
| `dotnet format ... --verify-no-changes` | 通過 |
| `mutation-probe.ps1` | 12/12 有鑑別力；還原後 48/48、110/110 |
| `generate-er-diagram.ps1 -Check` | ER 圖與資料字典一致 |
| `check-db-clean.ps1` | 沒有 `itest%` 殘留 |

## 重現順序

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/measure-dashboard-read-paths.ps1 -Baseline
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/measure-dashboard-read-paths.ps1 -Indexed
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/measure-dashboard-read-paths.ps1 -Cleanup
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/check-db-clean.ps1
```

`-Indexed` 只可在剛完成 `-Baseline`、資料仍在而 V007 尚未套用時使用；已套用 V007 後，使用 `-BaselineWithV007Invisible` 只為重現原始基準，該模式會在 `finally` 還原索引可見性。
