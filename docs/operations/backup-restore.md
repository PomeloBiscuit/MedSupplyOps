# 備份與還原演練（NFR-4）

> 最後檢查：2026-09-14。所有備份僅存放於本機 `backups/`，不進版本控制。

## 設計裁定

- **D1：邏輯備份以容器內 DBA 身分執行。** `MEDSUPPLY` 只有建表、序列、檢視表、程序與登入所需的最小權限，沒有角色，也沒有可見的 Oracle DIRECTORY。Data Pump 必須能讀寫 DIRECTORY；替應用帳號加權限會把維運工作永久擴張為應用程式權限。備份是維運職能，因此腳本以 `SYSDBA` 連到 `FREEPDB1` 執行 `expdp`／`impdp`，不 GRANT／REVOKE 應用帳號。
- **D2：兩層備份。** 邏輯 Data Pump schema export 可處理誤刪、結構回退與搬遷；volume tar cold backup 可救回容器或 named volume 完全遺失的情境。兩者分別演練。
- **D3：實體備份必須是 cold backup，而且要有「乾淨關機」的正面證據。** 固定順序是容器內 `shutdown immediate`（輸出必須同時出現 `Database closed`、`Database dismounted`、`ORACLE instance shut down` 三個標記）、`docker compose stop -t 120 oracle`、`docker inspect` 確認 `State.Status=exited` 且 `State.ExitCode` 為 `0` 或 `143`，才可 tar。**`137` 一律拒絕**（等待逾時被 SIGKILL，資料庫可能寫到一半）。★ 實測修正：這個 Oracle 映像的 PID 1 收到 SIGTERM 就是以 `143` 結束，`0` 根本拿不到；所以判準是「乾淨關機的三個標記 + 結束碼不是 137」，不是「結束碼必須是 0」。只檢查 `Exited` 字串會把被砍掉的資料庫複本當成備份。
- **D4：備份不進 repo。** `.gitignore` 排除 `backups/`，因為內容包括業務資料與 Identity 密碼雜湊。
- **D5：先證明目標為空，再還原。** 邏輯還原只接受 `DBA_TABLES` 中 `MEDSUPPLY` 為 0 個資料表的目標；實體還原只接受不存在的 named volume，並以 `docker compose create` 建立後驗證新 volume 為 0 個項目。非空目標一律拒絕還原。
- **D6：還原預設不執行。** `restore-database.ps1` 不帶 `-Force` 僅做備份與空目標檢查；必須顯式加 `-Force` 才會匯入或解壓。
- **D7：失敗檔案必須可辨識。** 從建立目錄起的任何失敗都會寫入 `UNTRUSTED.txt`，包含時間與原因；成功結尾不會印出，該資料夾內任何大小正常的 `.dmp`／`.tar` 都不得使用。
- **D8：重啟必須真的可用。** `docker compose start` 後會在有上限的迴圈中確認 `running|healthy`；只有回傳 0 而未 healthy 仍是失敗。
- **D9：tar 內容而非大小是守門。** 新 archive 顯式以 `oradata/` 為根目錄；建立與實體還原前均會列出內容並確認至少一個 `.dbf` 資料檔。還原仍相容舊的 volume-root archive。

## 容器內命令的傳遞方式

備份與還原在容器內執行 bash／SQL*Plus 時一律使用 `scripts/lib/ContainerExec.ps1`：它以無 BOM、LF 的暫存檔經 `docker cp` 傳入，而非把 PowerShell 5.1 的 stdin 直接管線給原生程式，避免 UTF-8 BOM 造成 bash 失敗或 SQL*Plus 安靜漏執行。

## 指令

```powershell
# 先建立並驗證兩層備份；通過前不得破壞資料庫。
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/backup-database.ps1

# 邏輯層演練：先以 SYSDBA DROP USER MEDSUPPLY CASCADE，再由腳本確認 0 表後還原。
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/restore-database.ps1 -Mode Logical -BackupDirectory backups\database-<timestamp> -Force

# 實體層演練：先 docker compose down -v，再由腳本確認 volume 不存在後還原。
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/restore-database.ps1 -Mode Physical -BackupDirectory backups\database-<timestamp> -Force
```

## 實際演練紀錄

所有操作均在本機 `D:\Project\MedSupplyOps` 與本機 Docker 引擎完成；沒有連線、掃描或上傳至外部主機。容器內確認 `ORACLE_PWD` 與 `APP_DB_PASSWORD` 均已設定，且 `MEDSUPPLY` 的角色數與 Data Pump DIRECTORY 權限皆為 0；全程沒有 GRANT／REVOKE。

### T1 — 先證明目標空白

邏輯層以 SYSDBA `DROP USER MEDSUPPLY CASCADE` 破壞，然後才呼叫還原腳本：

```text
T1_PRE_DESTRUCTION|items=5|identity_users=6|user_tables=15
T1_POST_DESTRUCTION|items=TABLE_NOT_FOUND|identity_users=TABLE_NOT_FOUND|user_tables=0
PRE_RESTORE|USER_TABLES=0|ITEMS=TABLE_NOT_FOUND|IDENTITY_USERS=TABLE_NOT_FOUND
T1_POST_RESTORE|items=5|identity_users=6|user_tables=15
```

實體層另以 `docker compose down -v` 破壞，證據為 `T1_PHYSICAL_POST_DESTRUCTION|VOLUME_EXISTS=False`；實體還原腳本亦輸出 `PRE_RESTORE|VOLUME_EXISTS=False` 與 `PRE_RESTORE|VOLUME_ENTRIES=0`，才解壓 archive。

### 已撤銷的 2026-09-10 實體備份宣稱

主機檔案為 `backups\database-20260910-113238\logical\medsupply-20260910-113238.dmp`（1,224,704 bytes）及 `physical\medsupply-oracle-data-20260910-113238.tar`（8,108,769,280 bytes）。`impdp SQLFILE` 的 `CREATE TABLE` 數為 15。匯出記錄實際列數包括：

```text
IDENTITY_USERS 6 rows; ITEMS 5 rows; STOCK_LOTS 11 rows; DEPARTMENTS 4 rows
SCHEMA_VERSIONS 4 rows; AUDIT_LOGS 0 rows; REQUISITIONS 0 rows
```

複製當下的記錄是 `medsupplyops-oracle   Exited (137) Less than a second ago`。這證明該 archive **不符合**新 D3，不能再稱為 cold backup，也不得用於任何還原演練。檔案大小與先前的 SQLFILE 驗證不會改變這個結論。

### T3 — 隱性資料庫物件

```text
T3_UTF8|email=keeper@example.local|dump=Typ=1 Len=9: e9,99,b3,e5,ba,ab,e7,ae,a1
T3_INDEXES|count=38
T3_INDEX|name=UX_ITEMS_CODE_ACTIVE|status=VALID
T3_CHECK|name=CK_STOCK_LOTS_QTY_NON_NEGATIVE|status=ENABLED
T3_IDENTITY_COLUMNS|count=9
T3_IDENTITY_INSERT|item_id=3513|item_code=NFR4-IDENTITY-PROBE
```

IDENTITY probe 隨即刪除。中文 DUMP 不含 `ef,bf,bd`。Oracle 26 對既有 V001 中文 COMMENT metadata 在 Data Pump 報 `ORA-39346`；這是已存在的 COMMENT 編碼問題，SQLFILE 結構驗證排除 COMMENT，而實體還原保留 volume 原樣。核心資料、Identity、約束、索引與 App 驗證均通過，未把該警告偽裝成成功。

### T5、T6 — App 與最終基線

```text
T5_ANONYMOUS_API=401
T5_KEEPER_LOGIN=302|LOCATION=/
T5_KEEPER_INVENTORY=200
T5_REQUESTER_CREATE=302|LOCATION=/Requisitions/Details/5448
T5_AUDIT|audit_log_id=1010|entity_type=Requisition|entity_id=5448|action=Create|actor=requester@example.local
T5_CLEANUP|requisitions=0|audit_logs=0|stock_lots=11
T6|items=5|departments=4|stock_lots=11|requisitions=0|audit_logs=0|identity_users=6|schema_versions=4|user_tables=15|user_indexes=38
```

Keeper 沒有建立請領單的 Policy，故登入與庫存頁驗證用 keeper；寫入則用有 `RequisitionCreate` 權限的 requester，並立即刪除請領單、稽核列，不改庫存。

## 備份還原實測（2026-09-14）

全部命令只操作本機 Docker；未對外連線。這一輪的目的不是把檔案湊出來，而是驗證
「不可信就不得看起來像成功」。目前發現 Oracle 26 Free 映像的 PID 1 生命週期與 D3 的
「compose stop 後 exit code 0」不相容，故 T2／T4 不可誠實地宣告通過。

### R-T1 — 舊腳本重現

舊腳本在 `backups\database-20260914-093613` 產生 `.dmp`（1,228,800 bytes）和 tar；直接讀取
容器得到：

```text
exited|unhealthy|137|2026-09-14T01:37:22.251986686Z
medsupply-20260914-093613.dmp              1228800
medsupply-oracle-data-20260914-093613.tar  5356738560
```

這證實「只用 `^Exited` 守門」會放過 137。此輪工具的即時輸出在 Data Pump 大量訊息後截斷，
無法證明舊腳本沒有印出成功結尾；後續直接檢查顯示容器已由舊腳本重啟為 `running|healthy|0`，
所以「restart 必定失敗且不印成功」**沒有在本機重現**，不得把它寫成實測事實。

### R-T2 — 新守門揭露映像不相容

新腳本已成功執行 SQL*Plus 的 `shutdown immediate`，並明確用了 `docker compose stop -t 120 oracle`；
但 Oracle 映像 PID 1 的 `wait` 被 SIGTERM 中斷，因此 Docker 記錄的是 143：

```text
ORACLE instance shut down.
CLEAN_SHUTDOWN|shutdown immediate completed
CONTAINER=medsupplyops-oracle STATUS=exited EXIT_CODE=143
UNTRUSTED|D:\Project\MedSupplyOps\backups\database-20260914-094621\UNTRUSTED.txt
備份失敗：Oracle 容器結束碼是 143，不是 0；拒絕建立不可信的實體備份。
HEALTHCHECK|running|healthy
```

這是正確的 fail-closed 行為，且容器已恢復 healthy；但不是 D3 所要求的 0，故沒有 tar、沒有成功結尾、沒有可信的 T2 備份。停止在此，未拿這個資料夾做還原。

**★ 修正判準後重跑（2026-09-14）**：判準改為「乾淨關機的三個標記 + 結束碼不是 137」（見 D3 的實測修正），
腳本據此重跑，備份成功：

```text
Database closed. / Database dismounted. / ORACLE instance shut down.
CONTAINER=medsupplyops-oracle STATUS=exited EXIT_CODE=143
archive-contents：oradata/*.dbf 存在
兩層備份完成
HEALTHCHECK|running|healthy
```

產物：`backups\database-20260914-113119\`（logical `.dmp` 1,228,800 bytes；physical tar 5,559,203,840 bytes；無 `UNTRUSTED.txt`）。

### R-T3 — 故意失敗不得留下假備份

用不存在的 volume 執行，未變更任何 compose 設定：

```text
Error response from daemon: get medsupplyops-oracle-data-does-not-exist: no such volume
UNTRUSTED|backups\T3-fault\database-20260914-100335\UNTRUSTED.txt
T3_BACKUP_EXIT=1
```

`UNTRUSTED.txt` 存在，沒有「兩層備份完成」。

### R-T4 — 還原：假失敗已修，成功路徑尚未演練

**已修**：還原腳本原本依主控台輸出判斷成敗，`impdp` 明明印出 `successfully completed` 仍被判為失敗（假紅燈）。
現在改以**容器內的 `impdp` log 檔內容**加上退出碼判定，不再依賴主控台字串。

**實測（2026-09-14）— 真失敗案例**：指向不存在的 `.dmp` 執行還原：

```text
找不到有效的邏輯 .dmp 備份。
restore(fail case) EXIT=1
```

失敗會大聲失敗，沒有宣稱成功。

**真成功案例（覆蓋式還原）尚未執行**：依 D5，邏輯還原前必須先以 SYSDBA `DROP USER MEDSUPPLY CASCADE`
把目標清空；那是不可逆操作，需要明確授權。**在它補做之前，本文件不宣稱「還原演練通過」。**

**★ 驗收方式的修正（實測發現）**：備份內容與當下資料庫相同時，「還原後列數一致」**連一個什麼都沒做的還原也會通過**。
所以成功路徑的驗收是：還原前先寫入一筆標記資料，還原後**那筆標記必須消失**，且四張表列數回到備份當時的值。
另已實測：對**非空** schema 執行還原會被腳本拒絕（`目標 MEDSUPPLY schema 不是空的；拒絕還原`），
這道守門正是防止「匯入到既有資料上、卻回報成功」。

### R-T5 — 137 的拒絕路徑（新判準的鑑別力）

乾淨關機之後停容器砍不到它（再短的逾時都是 143），所以要重現 137 必須**跳過乾淨關機**、在資料庫還活著時停容器：

```text
CONTAINER=medsupplyops-oracle STATUS=exited EXIT_CODE=137
UNTRUSTED|backups\T5-137\...\UNTRUSTED.txt
備份失敗：Oracle 容器結束碼是 137，不是允許的 0 或 143；拒絕建立不可信的實體備份。
HEALTHCHECK|running|healthy
```

新判準確實擋得住被砍掉的資料庫複本，而且容器可以恢復。
### R — 非備份關卡

```text
dotnet build MedSupplyOps.slnx --nologo                         0 warnings, 0 errors
dotnet test MedSupplyOps.slnx --no-build --nologo               domain 48 passed; integration 107 passed
dotnet format MedSupplyOps.slnx --verify-no-changes             exit 0
generate-er-diagram.ps1 -Check                                  ER 圖與資料字典一致
check-db-clean.ps1                                               資料庫乾淨
```
