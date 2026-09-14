-- 只刪除儀表板讀取路徑的效能資料。若沒有執行到這支，check-db-clean.ps1 會把 itest-perf 視為殘留並失敗。
WHENEVER SQLERROR EXIT SQL.SQLCODE ROLLBACK

ALTER SESSION SET CONTAINER = FREEPDB1;
ALTER SESSION SET CURRENT_SCHEMA = MEDSUPPLY;

DELETE FROM audit_logs WHERE actor = 'itest-perf';
DELETE FROM stock_lots WHERE created_by = 'itest-perf';
COMMIT;

PROMPT PERF_AUDIT_LOG_ROWS_AFTER_CLEANUP
SELECT COUNT(*) AS audit_log_count FROM audit_logs WHERE actor = 'itest-perf';
PROMPT PERF_STOCK_LOT_ROWS_AFTER_CLEANUP
SELECT COUNT(*) AS stock_lot_count FROM stock_lots WHERE created_by = 'itest-perf';
