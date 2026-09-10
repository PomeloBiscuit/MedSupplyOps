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
