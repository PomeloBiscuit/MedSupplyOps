-- 院內身分與稽核軌跡需要穩定的員工編號；既有帳號可維持 NULL。
-- Oracle 的一般唯一索引不會索引全 NULL 的鍵，因此可容許多筆 NULL，
-- 同時拒絕兩筆相同的非 NULL 員工編號。
ALTER TABLE identity_users ADD employee_no VARCHAR2(32 CHAR);

CREATE UNIQUE INDEX ux_identity_users_employee_no
    ON identity_users (employee_no);

COMMENT ON COLUMN identity_users.employee_no IS '院內員工編號；既有或不適用帳號可為 NULL，非 NULL 值不可重複。';
