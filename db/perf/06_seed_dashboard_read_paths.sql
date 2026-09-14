-- 首頁儀表板讀取路徑的一次性效能量測資料。所有資料皆以 itest-perf 標記，
-- check-db-clean.ps1 可在中斷後辨認它，09_cleanup_dashboard_read_paths.sql 會清除它。
--
-- AUDIT_LOGS：200,000 筆，最新 50,000 筆刻意共用 occurred_at，驗證
-- ORDER BY occurred_at DESC, audit_log_id DESC 的第二排序鍵不是可省略的裝飾。
-- STOCK_LOTS：20,000 筆，僅 100 筆已過期且仍有庫存（0.5%），模擬少數
-- 過期品項仍留在庫的警示路徑，足以檢驗 expiry_date 前導索引的必要性。
WHENEVER SQLERROR EXIT SQL.SQLCODE ROLLBACK

ALTER SESSION SET CONTAINER = FREEPDB1;
ALTER SESSION SET CURRENT_SCHEMA = MEDSUPPLY;

DELETE FROM audit_logs WHERE actor = 'itest-perf';
DELETE FROM stock_lots WHERE created_by = 'itest-perf';

DECLARE
    v_item_id items.item_id%TYPE;
BEGIN
    SELECT item_id
      INTO v_item_id
      FROM items
     WHERE item_code = 'MD-0001';

    FOR i IN 1 .. 200000 LOOP
        INSERT INTO audit_logs (entity_type, entity_id, action, actor, occurred_at)
        VALUES (
            CASE MOD(i, 4)
                WHEN 0 THEN 'Requisition'
                WHEN 1 THEN 'Item'
                WHEN 2 THEN 'StockLot'
                ELSE 'Unknown'
            END,
            TO_CHAR(v_item_id),
            'PerformanceSeed',
            'itest-perf',
            CASE
                WHEN i > 150000 THEN TIMESTAMP '2026-09-14 09:00:00'
                ELSE TIMESTAMP '2026-09-13 09:00:00' - NUMTODSINTERVAL(150000 - i, 'SECOND')
            END);
    END LOOP;

    FOR i IN 1 .. 20000 LOOP
        INSERT INTO stock_lots (
            item_id, lot_number, expiry_date, quantity, storage_location, created_by)
        VALUES (
            v_item_id,
            'ITEST-PERF-' || TO_CHAR(i, 'FM00000'),
            CASE WHEN i <= 100 THEN DATE '2026-01-01' ELSE DATE '2027-06-01' END,
            10,
            'ITEST-PERF-A01',
            'itest-perf');
    END LOOP;

    COMMIT;
END;
/

PROMPT PERF_AUDIT_LOG_ROWS
SELECT COUNT(*) AS audit_log_count FROM audit_logs WHERE actor = 'itest-perf';
PROMPT PERF_STOCK_LOT_ROWS
SELECT COUNT(*) AS stock_lot_count FROM stock_lots WHERE created_by = 'itest-perf';
PROMPT PERF_EXPIRED_IN_STOCK_ROWS
SELECT COUNT(*) AS expired_in_stock_count
  FROM stock_lots
 WHERE created_by = 'itest-perf'
   AND quantity > 0
   AND expiry_date < DATE '2027-01-01';
