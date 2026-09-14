-- 首頁稽核清單固定依 occurred_at DESC、audit_log_id DESC 取最新 5 筆。
-- 第二鍵使同一時間戳的順序確定；此索引同時提供完整排序，避免先掃描／JOIN
-- 全部 audit_logs 再做 STOPKEY 排序。量測證據：docs/performance/dashboard-read-paths.md。
CREATE INDEX ix_audit_logs_dashboard_feed
    ON audit_logs (occurred_at DESC, audit_log_id DESC);
