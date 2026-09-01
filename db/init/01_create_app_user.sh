#!/bin/bash
# ─────────────────────────────────────────────────────────────────────────────
# 建立應用程式帳號 MEDSUPPLY（最小權限）。冪等：已存在就跳過。
#
# 掛載於 /opt/oracle/scripts/startup —— 每次容器啟動都會跑，不是只跑一次。
#
# ★ 為什麼不是掛在 setup：
#   Oracle 的 runOracle.sh 只有在「實際建立新資料庫」時才執行 setup 目錄的腳本。
#   而 Oracle Free 的官方映像內含預建資料庫，首次啟動走的是「掛載既有資料庫」路徑，
#   setup 永遠不會被觸發。實測結果：容器 15 秒就 healthy、log 顯示
#   DATABASE IS READY TO USE，但 dba_users 裡沒有 MEDSUPPLY ——
#   如果只看健康檢查就往下做，會在連線階段才發現，而且症狀是「帳號密碼錯誤」。
#
# ★ 為什麼要有這個帳號（不用 SYSTEM）：
#   SYS / SYSTEM 是資料庫的最高權限帳號。應用程式拿它連線的話，
#   一個 SQL injection 就等於整台資料庫失守，而且稽核軌跡分不出是誰做的。
#
# ⚠ 本腳本被 runUserScripts.sh 以 source（`.`）方式載入，不可用 set -e / exit，
#   那會終止父行程。錯誤處理一律用 if 判斷。
# ─────────────────────────────────────────────────────────────────────────────

# ★ 必須設 NLS_LANG，否則 sqlplus 會用容器 OS 的預設字元集去解讀 UTF-8 的 .sql 檔。
#   症狀極度隱蔽：DDL 與 ASCII 資料完全正常、腳本回報成功、所有測試全綠 ——
#   只有中文欄位的內容在寫進資料庫「之前」就被換成 U+FFFD（EF BF BD）。
#   資料庫本身是 AL32UTF8，所以它忠實地存下了那些替代字元，不會報任何錯。
#   驗證方式只有一個：SELECT DUMP(欄位, 16)，用肉眼看 SELECT 結果會被
#   主控台編碼再騙一次。
export NLS_LANG=.AL32UTF8

echo "[medsupplyops] ===== 應用帳號 ====="

if [ -z "${APP_DB_PASSWORD}" ]; then
  echo "[medsupplyops] x APP_DB_PASSWORD 未設定，跳過。請檢查 .env。"
else
  USER_COUNT=$("${ORACLE_HOME}/bin/sqlplus" -s "/ as sysdba" <<'EOSQL' 2>/dev/null
SET HEADING OFF
SET FEEDBACK OFF
SET PAGESIZE 0
SET TRIMSPOOL ON
ALTER SESSION SET CONTAINER = FREEPDB1;
SELECT COUNT(*) FROM dba_users WHERE username = 'MEDSUPPLY';
EXIT
EOSQL
)
  USER_COUNT=$(echo "${USER_COUNT}" | tr -d '[:space:]')

  if [ "${USER_COUNT}" = "1" ]; then
    echo "[medsupplyops] - MEDSUPPLY 已存在，跳過建立。"
  else
    echo "[medsupplyops] 建立 MEDSUPPLY ..."
    "${ORACLE_HOME}/bin/sqlplus" -s "/ as sysdba" <<EOSQL
SET FEEDBACK ON
ALTER SESSION SET CONTAINER = FREEPDB1;

CREATE USER medsupply
  IDENTIFIED BY "${APP_DB_PASSWORD}"
  DEFAULT TABLESPACE users
  TEMPORARY TABLESPACE temp
  QUOTA UNLIMITED ON users;

-- 只給這個應用實際需要的權限。
GRANT CREATE SESSION   TO medsupply;
GRANT CREATE TABLE     TO medsupply;
GRANT CREATE SEQUENCE  TO medsupply;
GRANT CREATE VIEW      TO medsupply;
GRANT CREATE PROCEDURE TO medsupply;

-- 刻意不給：
--   DBA / RESOURCE        -- 遠超過需求；RESOURCE 在部分版本隱含 UNLIMITED TABLESPACE
--   UNLIMITED TABLESPACE  -- 已用 QUOTA 限定在 users 表空間
--   CREATE ANY TABLE / SELECT ANY TABLE  -- 應用程式不該碰別的 schema
--   CREATE PUBLIC SYNONYM -- 會污染全域命名空間
EXIT
EOSQL
  fi

  # ★ 不相信上面的訊息，實際查資料字典確認。
  #   sqlplus 即使建立失敗也可能回傳 0，只看「有沒有報錯」不足以判定成功。
  VERIFY=$("${ORACLE_HOME}/bin/sqlplus" -s "/ as sysdba" <<'EOSQL' 2>/dev/null
SET HEADING OFF
SET FEEDBACK OFF
SET PAGESIZE 0
ALTER SESSION SET CONTAINER = FREEPDB1;
SELECT username || '/' || account_status FROM dba_users WHERE username = 'MEDSUPPLY';
EXIT
EOSQL
)
  if echo "${VERIFY}" | grep -q 'MEDSUPPLY/OPEN'; then
    echo "[medsupplyops] OK  MEDSUPPLY 存在且為 OPEN。"
  else
    echo "[medsupplyops] x   MEDSUPPLY 驗證失敗，實際查詢結果："
    echo "${VERIFY}"
  fi
fi

echo "[medsupplyops] ===== 應用帳號處理結束 ====="
