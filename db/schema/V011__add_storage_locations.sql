-- 儲藏位置主檔刻意不以外鍵連到 STOCK_LOTS：既有批次以文字保存位置，
-- 改成外鍵前必須先完成歷史資料清理與大量服務／測試改寫。
CREATE TABLE storage_locations (
    location_id             NUMBER(19)      GENERATED ALWAYS AS IDENTITY,
    location_code           VARCHAR2(32 CHAR)   NOT NULL,
    name                    VARCHAR2(64 CHAR)   NOT NULL,
    name_en                 VARCHAR2(200 CHAR),

    is_deleted              NUMBER(1)       DEFAULT 0 NOT NULL,
    deleted_at              TIMESTAMP(6),
    deleted_by              VARCHAR2(100 CHAR),
    created_at              TIMESTAMP(6)    DEFAULT SYS_EXTRACT_UTC(SYSTIMESTAMP) NOT NULL,
    created_by              VARCHAR2(100 CHAR)  NOT NULL,

    CONSTRAINT pk_storage_locations PRIMARY KEY (location_id),
    CONSTRAINT uq_storage_locations_code UNIQUE (location_code),
    CONSTRAINT uq_storage_locations_name UNIQUE (name),
    CONSTRAINT ck_storage_locations_code_ascii CHECK (
        NOT REGEXP_LIKE(location_code, '[^!-~]')
    ),
    CONSTRAINT ck_storage_locations_deleted CHECK (is_deleted IN (0, 1)),
    CONSTRAINT ck_storage_locations_delete_consistent CHECK (
        (is_deleted = 0 AND deleted_at IS NULL AND deleted_by IS NULL)
        OR (is_deleted = 1 AND deleted_at IS NOT NULL AND deleted_by IS NOT NULL)
    )
);

COMMENT ON TABLE storage_locations IS '儲藏位置主檔；與 STOCK_LOTS 以名稱比對，但刻意不建立外鍵。';
COMMENT ON COLUMN storage_locations.location_code IS 'ASCII 識別碼；建立後不可修改。';
COMMENT ON COLUMN storage_locations.name IS '儲藏位置原文名稱；建立後不可修改，並與 STOCK_LOTS.STORAGE_LOCATION 比對。';
COMMENT ON COLUMN storage_locations.name_en IS '儲藏位置英文名稱；選填，顯示時空值回退原文。';

-- V002 已套用的環境不會重跑；在新表存在後由本 migration 補齊主檔與英文名稱。
INSERT INTO storage_locations (location_code, name, name_en, created_by)
VALUES ('CENTRAL-A01', '中央庫房-A01', 'Central Warehouse A01', 'migration-V011');

INSERT INTO storage_locations (location_code, name, name_en, created_by)
VALUES ('CENTRAL-A02', '中央庫房-A02', 'Central Warehouse A02', 'migration-V011');

INSERT INTO storage_locations (location_code, name, name_en, created_by)
VALUES ('CENTRAL-B01', '中央庫房-B01', 'Central Warehouse B01', 'migration-V011');

INSERT INTO storage_locations (location_code, name, name_en, created_by)
VALUES ('CENTRAL-C01', '中央庫房-C01', 'Central Warehouse C01', 'migration-V011');

INSERT INTO storage_locations (location_code, name, name_en, created_by)
VALUES ('CENTRAL-D01', '中央庫房-D01', 'Central Warehouse D01', 'migration-V011');

INSERT INTO storage_locations (location_code, name, name_en, created_by)
VALUES ('CENTRAL-E01', '中央庫房-E01', 'Central Warehouse E01', 'migration-V011');

COMMIT;
