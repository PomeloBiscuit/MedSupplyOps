-- =============================================================================
-- MedSupplyOps — 請領人的科室列級授權
-- =============================================================================

ALTER TABLE identity_users ADD (
    department_id NUMBER(19)
);

ALTER TABLE identity_users ADD CONSTRAINT fk_identity_users_department
    FOREIGN KEY (department_id) REFERENCES departments (department_id);

CREATE INDEX ix_identity_users_department ON identity_users (department_id);

-- 既有示範請領人固定指派到第一個啟用科室；其餘角色維持 NULL，表示不受科室範圍限制。
UPDATE identity_users
SET department_id = (
    SELECT department_id
    FROM (
        SELECT department_id
        FROM departments
        WHERE is_active = 1
          AND is_deleted = 0
        ORDER BY department_code
    )
    WHERE ROWNUM = 1
)
WHERE normalized_user_name = 'REQUESTER@EXAMPLE.LOCAL';

COMMENT ON COLUMN identity_users.department_id IS
    '請領人所屬科室；庫管員與管理員為 NULL。用於請領單列級授權。';

COMMIT;
