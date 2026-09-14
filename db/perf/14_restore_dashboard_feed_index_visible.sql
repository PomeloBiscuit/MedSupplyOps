-- 13_make_dashboard_feed_index_invisible.sql 的配對還原，避免量測後意外改變產品索引狀態。
WHENEVER SQLERROR EXIT SQL.SQLCODE ROLLBACK
ALTER SESSION SET CONTAINER = FREEPDB1;
ALTER INDEX MEDSUPPLY.ix_audit_logs_dashboard_feed VISIBLE;
