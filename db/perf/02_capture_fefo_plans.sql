-- 請由 scripts/measure-dapper-fefo.ps1 呼叫；該腳本會補上 sqlplus 連線資訊。
SET SQLBLANKLINES ON
SET LINESIZE 220
SET PAGESIZE 1000
SET LONG 100000
SET LONGCHUNKSIZE 100000
SET TRIMSPOOL ON
SET SERVEROUTPUT ON
WHENEVER SQLERROR EXIT SQL.SQLCODE ROLLBACK

PROMPT PERF_ROWS_AFTER_SEED
SELECT COUNT(*) AS stock_lots_count FROM stock_lots;

ALTER INDEX ix_stock_lots_fefo INVISIBLE;

BEGIN
    DBMS_STATS.GATHER_TABLE_STATS(USER, 'STOCK_LOTS', CASCADE => TRUE);
END;
/

VARIABLE itemId NUMBER
VARIABLE asOf VARCHAR2(10)
BEGIN
    SELECT item_id INTO :itemId FROM items WHERE item_code = 'MD-0001';
END;
/
EXEC :asOf := '2027-01-01';

PROMPT BEFORE_INDEX_INVISIBLE_AND_FULL_HINT
SET TERMOUT OFF
SELECT /*+ FULL(l) GATHER_PLAN_STATISTICS */ /* WO_D_FEFO_BEFORE */
       l.stock_lot_id,
       l.lot_number,
       l.expiry_date,
       l.quantity,
       l.storage_location
FROM stock_lots l
WHERE l.item_id = :itemId
  AND l.quantity > 0
  AND l.expiry_date >= TO_DATE(:asOf, 'YYYY-MM-DD')
ORDER BY l.expiry_date, l.lot_number, l.stock_lot_id;
SET TERMOUT ON
ALTER INDEX ix_stock_lots_fefo VISIBLE;

PROMPT AFTER_INDEX_VISIBLE
SET TERMOUT OFF
SELECT /*+ GATHER_PLAN_STATISTICS */ /* WO_D_FEFO_AFTER */
       l.stock_lot_id,
       l.lot_number,
       l.expiry_date,
       l.quantity,
       l.storage_location
FROM stock_lots l
WHERE l.item_id = :itemId
  AND l.quantity > 0
  AND l.expiry_date >= TO_DATE(:asOf, 'YYYY-MM-DD')
ORDER BY l.expiry_date, l.lot_number, l.stock_lot_id;
SET TERMOUT ON
