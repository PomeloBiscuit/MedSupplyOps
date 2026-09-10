# 備份與還原演練（NFR-4）

> 演練日期：2026-09-10。所有備份僅存放於本機 `backups/`，不進版本控制。

## 設計裁定

- **D1：邏輯備份以容器內 DBA 身分執行。** `MEDSUPPLY` 只有建表、序列、檢視表、程序與登入所需的最小權限，沒有角色，也沒有可見的 Oracle DIRECTORY。Data Pump 必須能讀寫 DIRECTORY；替應用帳號加權限會把維運工作永久擴張為應用程式權限。備份是維運職能，因此腳本以 `SYSDBA` 連到 `FREEPDB1` 執行 `expdp`／`impdp`，不 GRANT／REVOKE 應用帳號。
- **D2：兩層備份。** 邏輯 Data Pump schema export 可處理誤刪、結構回退與搬遷；volume tar cold backup 可救回容器或 named volume 完全遺失的情境。兩者分別演練。
- **D3：實體備份必須是 cold backup。** `docker compose stop` 後，確認 Oracle 容器為 `Exited` 才 tar volume。運行中複製資料檔可能在不同 SCN 取得片段：還原初期看似正常，卻在日後讀取或寫入才暴露不一致；這比立即失敗更危險。
- **D4：備份不進 repo。** `.gitignore` 排除 `backups/`，因為內容包括業務資料與 Identity 密碼雜湊。
- **D5：先證明目標為空，再還原。** 邏輯還原只接受 `DBA_TABLES` 中 `MEDSUPPLY` 為 0 個資料表的目標；實體還原只接受不存在的 named volume，並以 `docker compose create` 建立後驗證新 volume 為 0 個項目。非空目標一律拒絕還原。
- **D6：還原預設不執行。** `restore-database.ps1` 不帶 `-Force` 僅做備份與空目標檢查；必須顯式加 `-Force` 才會匯入或解壓。

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

### T2、T4 — 備份內容與 cold backup

主機檔案為 `backups\database-20260910-113238\logical\medsupply-20260910-113238.dmp`（1,224,704 bytes）及 `physical\medsupply-oracle-data-20260910-113238.tar`（8,108,769,280 bytes）。`impdp SQLFILE` 的 `CREATE TABLE` 數為 15。匯出記錄實際列數包括：

```text
IDENTITY_USERS 6 rows; ITEMS 5 rows; STOCK_LOTS 11 rows; DEPARTMENTS 4 rows
SCHEMA_VERSIONS 4 rows; AUDIT_LOGS 0 rows; REQUISITIONS 0 rows
```

複製當下的記錄：`medsupplyops-oracle   Exited (137) Less than a second ago`。運行中複製會讓資料檔落在不同 SCN，可能先「成功」而日後讀寫才暴露不一致；這比立即失敗更危險。

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
