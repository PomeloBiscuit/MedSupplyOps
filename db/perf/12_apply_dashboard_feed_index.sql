-- 只供儀表板讀取路徑同批資料的調校後量測使用。內容等同 V007，明確限定 owner，
-- 並寫入 schema_versions，讓容器下次 startup 不會重複套用 V007。
WHENEVER SQLERROR EXIT SQL.SQLCODE ROLLBACK

ALTER SESSION SET CONTAINER = FREEPDB1;
CREATE INDEX MEDSUPPLY.ix_audit_logs_dashboard_feed
    ON MEDSUPPLY.audit_logs (occurred_at DESC, audit_log_id DESC);
INSERT INTO MEDSUPPLY.schema_versions (version, script_name)
VALUES ('V007', 'V007__add_audit_log_dashboard_feed_index.sql');
COMMIT;

SELECT version, script_name
  FROM MEDSUPPLY.schema_versions
 WHERE version = 'V007';
