#!/bin/bash
# ─────────────────────────────────────────────────────────────────────────────
# 以 MEDSUPPLY 身分套用 db/schema/V*.sql。冪等：靠 SCHEMA_VERSIONS 表記錄已套用的版本。
#
# 為什麼要自己做一個最小的版本追蹤，而不是「每次都重跑 DDL」：
#   本腳本掛在 startup，每次容器啟動都會執行。沒有版本追蹤的話，
#   第二次啟動會滿螢幕 ORA-00955 (name is already used)，
#   真正的錯誤就被淹沒在雜訊裡 —— 而「一堆紅字但其實沒事」會訓練人忽略紅字。
#
# 為什麼用應用帳號而不是 SYSDBA 執行 DDL：
#   物件的擁有者就是執行 DDL 的帳號。用 SYSDBA 建，表會落在 SYS schema 下，
#   應用帳號反而存取不到，而且錯誤會延遲到第一次查詢才出現。
#
# ⚠ 被 source 載入，不可用 set -e / exit。
# ─────────────────────────────────────────────────────────────────────────────

echo "[medsupplyops] ===== Schema ====="

SCHEMA_DIR=/opt/oracle/scripts/schema
CONN="medsupply/${APP_DB_PASSWORD}@//localhost:1521/FREEPDB1"

if [ -z "${APP_DB_PASSWORD}" ]; then
  echo "[medsupplyops] x APP_DB_PASSWORD 未設定，跳過 schema。"
elif [ ! -d "${SCHEMA_DIR}" ]; then
  echo "[medsupplyops] x 找不到 ${SCHEMA_DIR}，請確認 docker-compose.yml 有掛載 ./db/schema。"
else
  # 版本追蹤表。用 PL/SQL 攔 ORA-00955 而不是先查再建 ——
  # 先查再建有 TOCTOU 空窗，雖然這裡是單執行緒，但養成習慣比較省事。
  "${ORACLE_HOME}/bin/sqlplus" -s "${CONN}" > /dev/null 2>&1 <<'EOSQL'
BEGIN
  EXECUTE IMMEDIATE 'CREATE TABLE schema_versions (
      version      VARCHAR2(16 CHAR)  NOT NULL,
      script_name  VARCHAR2(200 CHAR) NOT NULL,
      applied_at   TIMESTAMP(6)       DEFAULT SYS_EXTRACT_UTC(SYSTIMESTAMP) NOT NULL,
      CONSTRAINT pk_schema_versions PRIMARY KEY (version)
  )';
EXCEPTION
  WHEN OTHERS THEN
    IF SQLCODE != -955 THEN RAISE; END IF;   -- -955 = name is already used
END;
/
EXIT
EOSQL

  for f in $(ls "${SCHEMA_DIR}"/V*.sql 2>/dev/null | sort); do
    BASE=$(basename "$f")
    VERSION=$(echo "${BASE}" | sed 's/__.*//')     # V001__initial_schema.sql -> V001

    APPLIED=$("${ORACLE_HOME}/bin/sqlplus" -s "${CONN}" 2>/dev/null <<EOSQL
SET HEADING OFF
SET FEEDBACK OFF
SET PAGESIZE 0
SELECT COUNT(*) FROM schema_versions WHERE version = '${VERSION}';
EXIT
EOSQL
)
    APPLIED=$(echo "${APPLIED}" | tr -d '[:space:]')

    if [ "${APPLIED}" = "1" ]; then
      echo "[medsupplyops] - ${BASE} 已套用過，跳過。"
      continue
    fi

    echo "[medsupplyops] 執行 ${BASE} ..."
    OUTPUT=$("${ORACLE_HOME}/bin/sqlplus" -s "${CONN}" 2>&1 <<EOSQL
WHENEVER SQLERROR CONTINUE
SET ECHO OFF
SET FEEDBACK OFF
SET DEFINE OFF
-- ★ SQL*Plus 預設 (SQLBLANKLINES OFF) 會把敘述中間的空白行當成「敘述結束」，
--   於是一個跨多行、中間有空行分隔的 CREATE TABLE 會被切成好幾段，
--   後半段變成 SP2-0734 unknown command。
--   症狀有迷惑性：錯誤訊息指向欄位名稱（"unknown command beginning is_deleted..."），
--   讓人以為是那個欄位的語法錯，實際上是上面那個空行把敘述切斷了。
SET SQLBLANKLINES ON
@${f}
EXIT
EOSQL
)

    # ★ 不看離開碼，直接找錯誤字串。
    #   在 WHENEVER SQLERROR CONTINUE 之下，sqlplus 即使有語法錯誤也會回傳 0 ——
    #   只看 $? 會得到「全部成功」的假象，錯誤訊息就靜靜地留在輸出裡。
    if echo "${OUTPUT}" | grep -qE '(ORA|SP2|PLS)-[0-9]+'; then
      echo "[medsupplyops] x ${BASE} 有錯誤，不記錄為已套用："
      echo "${OUTPUT}" | grep -E '(ORA|SP2|PLS)-[0-9]+' | head -20
    else
      "${ORACLE_HOME}/bin/sqlplus" -s "${CONN}" > /dev/null 2>&1 <<EOSQL
INSERT INTO schema_versions (version, script_name) VALUES ('${VERSION}', '${BASE}');
COMMIT;
EXIT
EOSQL
      echo "[medsupplyops] OK ${BASE} 完成並記錄為 ${VERSION}。"
    fi
  done

  # ★ 最後查資料字典確認，而不是相信上面的訊息。
  echo "[medsupplyops] --- MEDSUPPLY schema 現況 ---"
  "${ORACLE_HOME}/bin/sqlplus" -s "${CONN}" <<'EOSQL'
SET PAGESIZE 60
SET LINESIZE 130
SET FEEDBACK OFF
COLUMN table_name FORMAT A26
COLUMN cons FORMAT 9999
COLUMN idx FORMAT 9999
SELECT t.table_name,
       (SELECT COUNT(*) FROM user_constraints c WHERE c.table_name = t.table_name) AS cons,
       (SELECT COUNT(*) FROM user_indexes i     WHERE i.table_name = t.table_name) AS idx
FROM   user_tables t
ORDER  BY t.table_name;
EXIT
EOSQL
fi

echo "[medsupplyops] ===== Schema 處理結束 ====="
