# 冷啟驗證紀錄

驗證日期：2026-09-10（本機 Windows PowerShell 5.1）。來源為
`D:\Project\MedSupplyOps` 的 commit `df77e98c18ea4249070746722de7a2ac5144ace1`；先確認
`git status --short` 無輸出，再以本機 clone 建立 `D:\Project\_coldstart-check`。沒有連線、掃描或上傳至外部主機。

**結論：冷啟資料庫成功，但 README 宣稱的流程未通過。** 已依停止條件停止；未重跑失敗測試、未執行六道關卡的其餘命令，亦未修改 `db/init`、`db/schema`、`docker-compose.yml`、`src`、`tests` 或任何既有關卡腳本。

## T0 — 破壞前的安全網

下列檔案在破壞前存在，且位元組大小與預期值一致：

```text
D:\Project\MedSupplyOps\backups\database-20260910-125052\logical\medsupply-20260910-125052.dmp                 1,224,704 bytes
D:\Project\MedSupplyOps\backups\database-20260910-125052\physical\medsupply-oracle-data-20260910-125052.tar   8,108,769,280 bytes
```

## T1 — 新 volume 的證據

```text
# down -v 前：docker volume ls --format '{{.Name}}'
medsupplyops-oracle-data

# docker compose down -v 後：docker volume ls --format '{{.Name}}'
<no output>

# clean clone 中 docker compose up -d 後：docker volume ls --format '{{.Name}}'
medsupplyops-oracle-data

# docker volume inspect medsupplyops-oracle-data
CreatedAt=2026-09-10T05:59:02Z
Labels={"com.docker.compose.project":"medsupplyops", ...}
```

`up -d` 的輸出為 `Volume medsupplyops-oracle-data Creating`、`Created`；沒有出現舊 volume 專案標籤不符的警告。這是新 volume，而非 2026-08-23 的 volume。

## T2、T3 — migration 與中文

容器日誌依序顯示 V001 至 V005 都以 `OK` 完成；`SCHEMA_VERSIONS` 的實際完整內容如下，五筆均在這次冷啟的幾秒內寫入：

```text
V001|V001__initial_schema.sql|2026-09-10 05:59:14.806823
V002|V002__seed_data.sql|2026-09-10 05:59:15.193194
V003|V003__identity.sql|2026-09-10 05:59:15.486773
V004|V004__authorization_department.sql|2026-09-10 05:59:16.034739
V005|V005__repair_v001_comments.sql|2026-09-10 05:59:16.226711
```

容器內 SQL 一律用 `scripts/lib/ContainerExec.ps1` 的無 BOM／LF 暫存檔與 `docker cp` 路徑執行。實際查詢結果：

```text
T3_DUMP|MD-0001|Typ=1 Len=18: e7,84,a1,e8,8f,8c,e6,aa,a2,e6,9f,a5,e6,89,8b,e5,a5,97
T3_TAB_COMMENTS_FFFD|0
T3_COL_COMMENTS_FFFD|0
```

上述 DUMP 是「無菌檢查手套」的正確 UTF-8 位元組，沒有 `ef,bf,bd`。

## T4 — Identity 帳號的時間點

在 cold-start schema 完成、尚未啟動 App 或測試時：

```text
T4_COLD_IDENTITY_USERS|0
```

README 的 `dotnet test` 執行後，即使其中一條整合測試失敗，帳號為 6：

```text
T4_AFTER_README_TEST_COUNT|6
admin@example.local|NULL
itest-admin@example.local|NULL
itest-keeper@example.local|NULL
itest-requester@example.local|3
keeper@example.local|NULL
requester@example.local|3
```

三個沒有 `itest-` 前綴的示範帳號由產品的 `DemoAccountSeeder` 冪等建立；三個 `itest-*` 帳號由整合測試的 `TestIdentitySeeder` 建立。因測試在失敗點停止驗證，未把「全部跑完」宣稱為成功；當下可觀察到的帳號數已是 6。

## T5 — README 逐條實測

```text
cp .env.example .env
```

在 Windows PowerShell 5.1 中以 `cp`（`Copy-Item` alias）成功建立未追蹤 `.env`，再填入符合 Oracle 規則的本機測試密碼。

```text
docker compose up -d
```

成功建立 network、`medsupplyops-oracle-data` 與 `medsupplyops-oracle`。`up -d` 開始時間為 `2026-09-10T13:59:01.8268750+08:00`；日誌的 `DATABASE IS READY TO USE!` 出現在容器時間 `2026-09-10 05:59:11 UTC`，約 9 秒後，但它**早於**使用者 startup scripts。V005 完成於 `05:59:16.226711 UTC`，故真正可供 App／測試使用約在 14 秒後。

```text
dotnet test MedSupplyOps.slnx --nologo
```

實際輸出先顯示 Domain `Passed: 48 / Total: 48`，接著整合測試失敗：

```text
MedSupplyOps.Integration.Tests.Web.AuthorizationAndAuditTests.Requester_direct_url_to_another_department_is_forbidden [FAIL]
System.InvalidOperationException : Sequence contains no elements
  at Dapper.SqlMapper.QueryRowAsync ...
  at ...AuthorizationAndAuditTests.GetRequesterDepartmentIdAsync()
  at ...Requester_direct_url_to_another_department_is_forbidden()
```

該 helper 的 SQL 是以 `QuerySingleAsync` 查 `identity_users` 中
`normalized_user_name = ITEST-REQUESTER@EXAMPLE.LOCAL`；失敗後的 read-only 查詢則顯示該帳號與 `department_id=3` 已存在。這次單一冷啟執行不足以判定其競態／生命週期原因，依約定不重試同一做法。

README 原本要求等 `DATABASE IS READY TO USE!`，但這個 marker 在 user-defined startup scripts 之前，照字面操作可能在 schema 未完成時就開始測試。因此 README 已改為等 `DONE: Executing user defined scripts`，並明示本次 cold-start test failure。除此之外，不需要額外步驟：`.env`、Compose 與 Oracle schema 都成功。

## 六道關卡與 T6

失敗後依停止條件，沒有執行 build、format、mutation-probe、ER check 或 check-db-clean；也沒有重跑 `dotnet test`。因此六道關卡**未完成，沒有全綠數字可回報**。

在失敗後立即以 read-only SQL 查到的狀態如下：

```text
items=5|departments=4|stock_lots=11|requisitions=0|audit_logs=0
identity_users=6|schema_versions=5|user_tables=15|user_indexes=38
```

其中資料表、索引、種子資料與 migrations 均符合基線；identity 亦已達到 6。已刪除 `D:\Project\_coldstart-check`；主工作樹在本紀錄提交後才應回到乾淨狀態。

## 後續修正建議（未執行）

先隔離整合測試 `AuthorizationAndAuditTests.Requester_direct_url_to_another_department_is_forbidden`，確認其 fixture 建立、`TestIdentitySeeder.SeedAsync` 與讀取帳號之間的生命週期；修正應限於能證明該帳號在每條相依測試開始前存在的測試／fixture 設計，再以全新 volume 重做本驗證。這不是 schema migration、種子資料、Compose 或權限問題；本次未做任何 GRANT／REVOKE。

---

## 修正與再驗證（2026-09-10）

> 上面到「後續修正建議」為止，是冷啟當下的原始紀錄，**刻意保留不改** ——
> 它記的是失敗的樣子，那本身就是這份文件的價值。以下是之後發生的事。

### 根因

那條測試的第一行就直連資料庫，查 `itest-requester@example.local` 綁在哪個科室，
**之後**才呼叫 `CreateClient()`。而 `itest-*` 帳號是 Host 啟動時由
`TestIdentitySeeder` 種進去的，`WebApplicationFactory` 又是惰性的 ——
不碰 `CreateClient()` 或 `Services` 就不會建 Host。

所以實際順序是：**查帳號 → 查不到 → 才建 Host → 才種帳號**。
在跑過幾輪的資料庫上，那些帳號是前幾輪留下來的，於是永遠查得到。

上面「後續修正建議」指出的方向**完全正確**（fixture 建立、seeder、讀取帳號三者的生命週期；
不是 schema、種子資料、Compose 或權限問題），修正即依此進行。

### 修正

`AuthorizationAndAuditTests` 補上 `IAsyncLifetime`，`InitializeAsync` 只做一件事：
強制 Host 先建起來（`_ = _factory.Services;`）。commit `d5133d3`。

五個 Web 測試類別中只有這一個沒有 `IAsyncLifetime`；另外四個在 `InitializeAsync` 裡登入，
順帶就把 Host 建起來了 —— 那個保護是別的目的的副作用，所以漏掉一個沒有人發現。

### 鑑別力（修正前後，同一個條件）

把 `itest-*` 帳號從資料庫刪光，等同全新資料庫的身分狀態：

```text
itest 帳號數 = 0
修正前  AuthorizationAndAuditTests.Requester_direct_url_to_another_department_is_forbidden
        -> Failed: System.InvalidOperationException : Sequence contains no elements
修正後  -> Passed
```

### 再驗證（在冷啟建出的同一個資料庫上）

資料庫仍是上面冷啟建出的那一個（volume `CreatedAt=2026-09-10T05:59:02Z`），
測試帳號清空後跑完整套：

```text
Domain.Tests        Passed 48 / Failed 0
Integration.Tests   Passed 68 / Failed 0
build 0 警告 0 錯誤／format 無差異／突變探針 9/9／ER 圖一致／資料庫乾淨
```

另確認沒有任何測試依賴示範帳號（`requester@`／`keeper@`／`admin@example.local`）
預先存在 —— 測試一律使用 `itest-*`；示範帳號由 `Program.cs` 啟動時建立。

### 冷啟之後環境沒有回到原狀（已修復，記錄以供追溯）

冷啟結束後刪除了乾淨副本目錄，但：

1. **容器仍掛在已刪除的目錄上**：`db/init`、`db/schema` 的 bind mount 來源是
   `D:\Project\_coldstart-check\...` —— 遷移機制因此失效。
2. **資料庫的 `MEDSUPPLY` 密碼是那份副本的 `.env` 設的**，主工作目錄因此連不上（`ORA-01017`）。
   `db/init/01_create_app_user.sh` 只做 `CREATE USER`、不會重設既有使用者的密碼，重啟救不回來。

修復：以 SYSDBA 把密碼校正回主工作目錄 `.env` 的值，再從主工作目錄重建容器。
掛載已確認指回 `D:\Project\MedSupplyOps\db\{init,schema}`。

⚠ 這代表冷啟演練的**收尾步驟**本身也需要驗證：刪掉乾淨副本之前，
必須先讓容器回到主工作目錄底下，並確認 `.env` 與資料庫密碼一致。

### 尚未做的事

**「乾淨 clone → 冷啟 → 六道關卡」一次跑到底的完整重演**尚未在修正後重做。
它慢且具破壞性，定位為**轉公開前的發佈關卡**。

---

## 發佈關卡①完整重演（2026-09-14）

來源主工作樹 `D:\Project\MedSupplyOps` 起始 commit 為
`c4c21d3cb8f5647e01cf58247e153d55a62e8f08`；開工前 `git status --short` 無輸出，
最近三筆歷史含 `939b70e`。在 repo 外建立本機 clone
`D:\Project\MedSupplyOps-coldstart-20260914-121637`，兩端 HEAD 完全相同；只複製主目錄既有的
`.env`，未輸出或記錄任何密碼。全程僅使用本機 Docker、local clone 與 NuGet restore。

### T0 — 安全網

冷啟前以既有備份腳本完成邏輯與實體兩層備份，並驗證 15 張資料表、乾淨關機、archive 內 22 個
`.dbf` 資料檔，以及重啟後 `running|healthy`：

```text
邏輯：D:\Project\MedSupplyOps\backups\database-20260914-121637\logical\medsupply-20260914-121637.dmp (1,228,800 bytes)
實體：D:\Project\MedSupplyOps\backups\database-20260914-121637\physical\medsupply-oracle-data-20260914-121637.tar (5,668,761,600 bytes)
CLEAN_SHUTDOWN|shutdown immediate completed
CONTAINER=medsupplyops-oracle STATUS=exited EXIT_CODE=143
HEALTHCHECK|running|healthy
```

### T1 — 全新 volume、migration 與 V006

只在 clean clone 中執行 `docker compose down -v`，輸出明確顯示
`Volume medsupplyops-oracle-data Removed`；後續 `up -d` 重新顯示 `Volume ... Created`。Oracle 日誌依序為
`OK V001`、`OK V002`、`OK V003`、`OK V004`、`OK V005`、`OK V006`，最後為
`DONE: Executing user defined scripts`。新資料庫的 `schema_versions` 完整內容：

```text
V001|V001__initial_schema.sql
V002|V002__seed_data.sql
V003|V003__identity.sql
V004|V004__authorization_department.sql
V005|V005__repair_v001_comments.sql
V006|V006__fix_stock_lots_comment.sql
U_FFFD|0
```

`SeedDataEncodingTests.Stock_lots_comment_matches_the_correct_chinese_text_exactly` 包含在下列 107 條
整合測試中並通過，故 V006 的逐字註解斷言已在**全新資料庫**上驗證。直接 SQL*Plus 顯示中文時會受
主控台字碼頁影響而呈現 `?`，不以該顯示結果取代精確的 .NET Unicode 字串斷言；資料字典 U+FFFD 掃描為 0。

### T2 — 六道關卡（第一次冷啟、修正與最終結果）

初次 `dotnet build` 為 0 warnings、0 errors。初次 README 原樣 `dotnet test` 的結果是 Domain 48/48、
Integration 106/107；唯一失敗為
`InventoryApiTests.GetExpiring_returns_camel_case_fields_and_usable_seed_lot_values`。這是明顯的測試碼缺陷，
不是產品／migration 缺陷：測試用固定的 `TestBusinessCalendar.Today` 查詢以 `TRUNC(SYSDATE)` 建立的種子批次。
新 DB 的 `GLO-FEFO-A` 是建庫日 +30，已落在固定假日期的 +30 範圍外；累積資料庫的舊建庫日恰好掩蓋問題。

屬於「明顯的測試碼缺陷，可直接修正」的例外，最小修正為改用 `TestBusinessCalendar.SystemToday`。
修正後第一次完整測試為 Domain 48/48、Integration 107/107（共 155）；最終 clean-clone build 再次為
0 warnings、0 errors。格式檢查曾抓到新加三行的 LF/CRLF 行尾差異，僅格式化該測試檔後通過。

```text
1 build                         成功；0 warnings、0 errors
2 dotnet test                   Domain 48/48；Integration 107/107；共 155 passed
3 dotnet format --verify        成功；無差異
4 mutation-probe                P1–P12 全有鑑別力；變紅數 2,2,1,4,2,1,3,9,5,2,1,1；還原後 48/48、107/107
5 generate-er-diagram -Check    ER 圖與資料字典一致
6 check-db-clean                0 筆 created_by / actor LIKE 'itest%' 殘留
```

### T3 — 接回主目錄

在刪除 clone 前，從主目錄執行 `docker compose up -d`；Docker 輸出 `Recreate`、`Recreated`、`Started`，
接著為 `HEALTHCHECK|running|healthy`。主目錄的實際結果：

```text
scripts/check-db-clean.ps1  資料庫乾淨：沒有 created_by / actor LIKE 'itest%' 的殘留資料。
dotnet test                 Domain 48/48；Integration 107/107；共 155 passed
```

主目錄可連線，表示容器掛載已指回主工作樹，且既有 `.env` 的連線設定有效。

### T4 — README

本次使用的順序與 README 的四行相同：既有 `.env`、`docker compose up -d`、等候
`DONE: Executing user defined scripts`、`dotnet test MedSupplyOps.slnx`。README **沒有缺少必要步驟**；
冷啟前為了刻意取得新 volume 額外執行的 `docker compose down -v` 是發佈驗證專用的破壞性前置，
不屬於一般使用者的「怎麼跑起來」流程。
