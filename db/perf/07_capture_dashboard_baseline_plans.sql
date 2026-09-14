-- 儀表板讀取路徑基準：每支產品讀取 SQL 連跑兩次。兩次用不同註解保留各自的 cursor，
-- 但 SELECT、bind 與結果集完全相同；08_display_dashboard_baseline_plans.sql 逐一顯示 ALLSTATS LAST。
SET SQLBLANKLINES ON
SET SERVEROUTPUT ON
WHENEVER SQLERROR EXIT SQL.SQLCODE ROLLBACK

ALTER SESSION SET CONTAINER = FREEPDB1;
ALTER SESSION SET CURRENT_SCHEMA = MEDSUPPLY;

BEGIN
    DBMS_STATS.GATHER_TABLE_STATS('MEDSUPPLY', 'AUDIT_LOGS', CASCADE => TRUE);
    DBMS_STATS.GATHER_TABLE_STATS('MEDSUPPLY', 'STOCK_LOTS', CASCADE => TRUE);
END;
/

VARIABLE asOf VARCHAR2(10)
VARIABLE count NUMBER
EXEC :asOf := '2027-01-01';
EXEC :count := 5;

SET TERMOUT OFF
SELECT /*+ GATHER_PLAN_STATISTICS */ /* WO_S_AUDIT_BASELINE_RUN_1 */
       a.entity_type AS EntityType, a.entity_id AS EntityId, a.action AS Action,
       a.actor AS Actor, a.occurred_at AS OccurredAt, u.display_name AS DisplayName,
       r.requisition_no AS RequisitionNo, i.item_code AS ItemCode, l.lot_number AS LotNumber
  FROM audit_logs a
  LEFT JOIN identity_users u ON u.user_name = a.actor
  LEFT JOIN requisitions r ON a.entity_type = 'Requisition' AND r.requisition_id = TO_NUMBER(a.entity_id DEFAULT NULL ON CONVERSION ERROR)
  LEFT JOIN items i ON a.entity_type = 'Item' AND i.item_id = TO_NUMBER(a.entity_id DEFAULT NULL ON CONVERSION ERROR)
  LEFT JOIN stock_lots l ON a.entity_type = 'StockLot' AND l.stock_lot_id = TO_NUMBER(a.entity_id DEFAULT NULL ON CONVERSION ERROR)
 ORDER BY a.occurred_at DESC, a.audit_log_id DESC
 FETCH FIRST :count ROWS ONLY;

SELECT /*+ GATHER_PLAN_STATISTICS */ /* WO_S_AUDIT_BASELINE_RUN_2 */
       a.entity_type AS EntityType, a.entity_id AS EntityId, a.action AS Action,
       a.actor AS Actor, a.occurred_at AS OccurredAt, u.display_name AS DisplayName,
       r.requisition_no AS RequisitionNo, i.item_code AS ItemCode, l.lot_number AS LotNumber
  FROM audit_logs a
  LEFT JOIN identity_users u ON u.user_name = a.actor
  LEFT JOIN requisitions r ON a.entity_type = 'Requisition' AND r.requisition_id = TO_NUMBER(a.entity_id DEFAULT NULL ON CONVERSION ERROR)
  LEFT JOIN items i ON a.entity_type = 'Item' AND i.item_id = TO_NUMBER(a.entity_id DEFAULT NULL ON CONVERSION ERROR)
  LEFT JOIN stock_lots l ON a.entity_type = 'StockLot' AND l.stock_lot_id = TO_NUMBER(a.entity_id DEFAULT NULL ON CONVERSION ERROR)
 ORDER BY a.occurred_at DESC, a.audit_log_id DESC
 FETCH FIRST :count ROWS ONLY;

SELECT /*+ GATHER_PLAN_STATISTICS */ /* WO_S_EXPIRED_BASELINE_RUN_1 */ COUNT(*)
  FROM stock_lots l
 INNER JOIN items i ON i.item_id = l.item_id
 WHERE i.is_deleted = 0 AND l.quantity > 0 AND l.expiry_date < TO_DATE(:asOf, 'YYYY-MM-DD');

SELECT /*+ GATHER_PLAN_STATISTICS */ /* WO_S_EXPIRED_BASELINE_RUN_2 */ COUNT(*)
  FROM stock_lots l
 INNER JOIN items i ON i.item_id = l.item_id
 WHERE i.is_deleted = 0 AND l.quantity > 0 AND l.expiry_date < TO_DATE(:asOf, 'YYYY-MM-DD');
SET TERMOUT ON
