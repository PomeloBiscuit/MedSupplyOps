-- =============================================================================
-- MedSupplyOps — 初始 schema（Oracle Database Free 23ai / 26ai）
--
-- 本檔為「手寫 DDL 優先」，不是 EF Core Migration 產生的。
-- 理由：資料庫的正確性保證（CHECK、UNIQUE、FK、索引設計）是這個系統的核心，
--       而 ORM 產生的 DDL 只會反映 C# 型別，不會反映業務不變式。
--       EF Core 在 Infrastructure 層對映到這份 schema，不是反過來。
--
-- 執行身分：MEDSUPPLY（應用帳號，非 SYSTEM）
-- 容器連線：//localhost:1521/FREEPDB1
-- =============================================================================

-- ─────────────────────────────────────────────────────────────────────────────
-- ★ 全域設計裁定 1：所有字串長度一律使用 CHAR 語意，不用預設的 BYTE 語意。
--
--   Oracle 的 VARCHAR2(50) 預設是「50 個 **位元組**」。
--   在 AL32UTF8 之下，一個中文字佔 3 個位元組 —— 所以 VARCHAR2(50) 只裝得下
--   16 個中文字。「王大明」沒問題，「小兒血液腫瘤科重症加護病房」就爆了。
--
--   這個錯誤的症狀是「大部分資料正常，偶爾某一筆新增失敗」，
--   而失敗訊息是 ORA-12899 value too large —— 開發者看到欄位長度 50、
--   輸入只有 20 個字，會完全想不通。
--   醫院系統幾乎全中文，這是必踩的坑，所以一律寫死 CHAR。
--
-- ★ 全域設計裁定 2：時間一律存 UTC 的 TIMESTAMP(6)，不存 DATE、不存本地時間。
--   Oracle 的 DATE 其實含時分秒但精度只到秒，容易與「純日期」混淆。
--   純日期欄位（效期）才用 DATE，並在註解標明「只有日期部分有意義」。
--
-- ★ 全域設計裁定 3：所有刪除都是軟刪除（FR-404 / MIG-2）。
--   若用 ON DELETE CASCADE 把明細掛在主檔下，刪一個科室就會連帶刪光它所有的
--   請領與發料紀錄 —— 而那正是事後追責唯一的依據。
--   在醫療場域中那等同於銷毀稽核軌跡。本 schema 沒有任何 CASCADE。
-- ─────────────────────────────────────────────────────────────────────────────


-- =============================================================================
-- 1. ITEMS — 醫材品項主檔（FR-101）
-- =============================================================================
CREATE TABLE items (
    item_id                 NUMBER(19)      GENERATED ALWAYS AS IDENTITY,
    item_code               VARCHAR2(32 CHAR)   NOT NULL,
    item_name               VARCHAR2(200 CHAR)  NOT NULL,
    specification           VARCHAR2(400 CHAR),
    unit_of_measure         VARCHAR2(20 CHAR)   NOT NULL,
    -- 是否管制批號／效期。並非所有耗材都需要（例如棉棒），FR-101 保留此彈性。
    tracks_lot              NUMBER(1)       DEFAULT 1 NOT NULL,
    tracks_expiry           NUMBER(1)       DEFAULT 1 NOT NULL,
    safety_stock_qty        NUMBER(10)      DEFAULT 0 NOT NULL,

    is_deleted              NUMBER(1)       DEFAULT 0 NOT NULL,
    deleted_at              TIMESTAMP(6),
    deleted_by              VARCHAR2(100 CHAR),
    created_at              TIMESTAMP(6)    DEFAULT SYS_EXTRACT_UTC(SYSTIMESTAMP) NOT NULL,
    created_by              VARCHAR2(100 CHAR)  NOT NULL,
    updated_at              TIMESTAMP(6),
    updated_by              VARCHAR2(100 CHAR),

    CONSTRAINT pk_items                 PRIMARY KEY (item_id),
    CONSTRAINT ck_items_tracks_lot      CHECK (tracks_lot IN (0, 1)),
    CONSTRAINT ck_items_tracks_expiry   CHECK (tracks_expiry IN (0, 1)),
    CONSTRAINT ck_items_is_deleted      CHECK (is_deleted IN (0, 1)),
    CONSTRAINT ck_items_safety_stock    CHECK (safety_stock_qty >= 0),
    -- 軟刪除的一致性：刪除旗標與刪除時間必須同時有值或同時為空，
    -- 否則會出現「已刪除但不知何時刪的」或「有刪除時間卻還在用的」資料。
    CONSTRAINT ck_items_delete_consistent CHECK (
        (is_deleted = 0 AND deleted_at IS NULL AND deleted_by IS NULL)
        OR (is_deleted = 1 AND deleted_at IS NOT NULL AND deleted_by IS NOT NULL)
    )
);

-- ★ 只對「未刪除」的資料強制料號唯一。
--   Oracle 的 B-tree 索引不會索引「鍵值全為 NULL」的列，
--   所以 CASE WHEN is_deleted = 0 THEN item_code END 這種函數索引，
--   等同於其他資料庫的 partial index / filtered index。
--   若直接對 item_code 建 UNIQUE，軟刪除後就永遠無法用回同一個料號 ——
--   而那個限制不會有任何錯誤訊息說明原因，使用者只會看到「料號重複」。
CREATE UNIQUE INDEX ux_items_code_active
    ON items (CASE WHEN is_deleted = 0 THEN item_code END);


-- =============================================================================
-- 2. DEPARTMENTS — 請領科室（FR-102）
-- =============================================================================
CREATE TABLE departments (
    department_id           NUMBER(19)      GENERATED ALWAYS AS IDENTITY,
    department_code         VARCHAR2(32 CHAR)   NOT NULL,
    department_name         VARCHAR2(200 CHAR)  NOT NULL,
    is_active               NUMBER(1)       DEFAULT 1 NOT NULL,

    is_deleted              NUMBER(1)       DEFAULT 0 NOT NULL,
    deleted_at              TIMESTAMP(6),
    deleted_by              VARCHAR2(100 CHAR),
    created_at              TIMESTAMP(6)    DEFAULT SYS_EXTRACT_UTC(SYSTIMESTAMP) NOT NULL,
    created_by              VARCHAR2(100 CHAR)  NOT NULL,
    updated_at              TIMESTAMP(6),
    updated_by              VARCHAR2(100 CHAR),

    CONSTRAINT pk_departments           PRIMARY KEY (department_id),
    CONSTRAINT ck_departments_active    CHECK (is_active IN (0, 1)),
    CONSTRAINT ck_departments_deleted   CHECK (is_deleted IN (0, 1)),
    CONSTRAINT ck_departments_delete_consistent CHECK (
        (is_deleted = 0 AND deleted_at IS NULL AND deleted_by IS NULL)
        OR (is_deleted = 1 AND deleted_at IS NOT NULL AND deleted_by IS NOT NULL)
    )
);

CREATE UNIQUE INDEX ux_departments_code_active
    ON departments (CASE WHEN is_deleted = 0 THEN department_code END);


-- =============================================================================
-- 3. STOCK_LOTS — 庫存批次（FR-201）
--    數量掛在批次上，不掛在品項上。一個品項有多個批次，各有自己的批號與效期。
-- =============================================================================
CREATE TABLE stock_lots (
    stock_lot_id            NUMBER(19)      GENERATED ALWAYS AS IDENTITY,
    item_id                 NUMBER(19)      NOT NULL,
    lot_number              VARCHAR2(64 CHAR)   NOT NULL,
    -- 有效期限。只有日期部分有意義；語意為「這一天（含）之前仍可使用」。
    expiry_date             DATE            NOT NULL,
    quantity                NUMBER(10)      NOT NULL,
    storage_location        VARCHAR2(64 CHAR)   NOT NULL,

    -- ★ 樂觀並發控制的版本欄位（FR-402）。每次更新由應用程式遞增。
    --   Oracle 沒有 SQL Server 那種自動遞增的 rowversion 型別，
    --   ORA_ROWSCN 雖然存在但預設是「區塊層級」的，同一個區塊裡的其他列被改也會變動，
    --   拿它當並發權杖會產生大量假衝突。所以用明確的版本欄位。
    row_version             NUMBER(19)      DEFAULT 0 NOT NULL,

    created_at              TIMESTAMP(6)    DEFAULT SYS_EXTRACT_UTC(SYSTIMESTAMP) NOT NULL,
    created_by              VARCHAR2(100 CHAR)  NOT NULL,
    updated_at              TIMESTAMP(6),
    updated_by              VARCHAR2(100 CHAR),

    CONSTRAINT pk_stock_lots        PRIMARY KEY (stock_lot_id),
    CONSTRAINT fk_stock_lots_item   FOREIGN KEY (item_id) REFERENCES items (item_id),
    -- ★★ 資料庫層的最後一道防線（FR-402）。
    --    應用程式的並發控制若有漏洞，超發會在這裡被擋成 ORA-02290 而不是寫出負庫存。
    --    「畫面正常、庫存變 -3」是這個系統最不能發生的事，所以不能只靠程式碼把關。
    CONSTRAINT ck_stock_lots_qty_non_negative CHECK (quantity >= 0),
    -- 同一品項、同一批號、同一儲位只能有一筆。
    CONSTRAINT ux_stock_lots_natural_key UNIQUE (item_id, lot_number, storage_location)
);

-- ★ FEFO 查詢的專用索引（NFR-3 的效能對照就用這支）。
--   查詢型態固定是：WHERE item_id = :id AND quantity > 0 AND expiry_date >= :asOf
--                  ORDER BY expiry_date, lot_number
--   把 expiry_date 放在 item_id 之後，讓索引同時滿足「篩選」與「排序」，
--   使執行計畫不需要 SORT ORDER BY 步驟。
--   驗收用「邏輯讀取次數（consistent gets）」比較，不用執行時間 ——
--   時間的變異來自作業系統排程與 GC，無法證明或證偽。
CREATE INDEX ix_stock_lots_fefo
    ON stock_lots (item_id, expiry_date, lot_number, stock_lot_id);


-- =============================================================================
-- 4. REQUISITIONS — 請領單（FR-301 / FR-304）
-- =============================================================================
CREATE TABLE requisitions (
    requisition_id          NUMBER(19)      GENERATED ALWAYS AS IDENTITY,
    requisition_no          VARCHAR2(32 CHAR)   NOT NULL,
    department_id           NUMBER(19)      NOT NULL,
    -- 狀態值與 C# 的 RequisitionStatus 列舉一一對應。
    -- 存字串而不是數字：數字在資料庫裡讀不出意義，而稽核軌跡是要給人看的。
    status                  VARCHAR2(20 CHAR)   NOT NULL,
    rejection_reason        VARCHAR2(500 CHAR),

    submitted_at            TIMESTAMP(6),
    approved_at             TIMESTAMP(6),
    issued_at               TIMESTAMP(6),
    closed_at               TIMESTAMP(6),

    row_version             NUMBER(19)      DEFAULT 0 NOT NULL,
    created_at              TIMESTAMP(6)    DEFAULT SYS_EXTRACT_UTC(SYSTIMESTAMP) NOT NULL,
    created_by              VARCHAR2(100 CHAR)  NOT NULL,
    updated_at              TIMESTAMP(6),
    updated_by              VARCHAR2(100 CHAR),

    CONSTRAINT pk_requisitions      PRIMARY KEY (requisition_id),
    CONSTRAINT uq_requisitions_no   UNIQUE (requisition_no),
    CONSTRAINT fk_requisitions_dept FOREIGN KEY (department_id) REFERENCES departments (department_id),
    -- 狀態只能是這六個。少了這條，一次打錯字就會產生一張永遠卡住的單，
    -- 而且它在列表頁看起來完全正常。
    CONSTRAINT ck_requisitions_status CHECK (
        status IN ('Draft', 'PendingApproval', 'Approved', 'Rejected', 'Issued', 'Closed')
    ),
    -- 駁回必須有原因，且只有已駁回的單才能有原因（FR-302）。
    CONSTRAINT ck_requisitions_rejection CHECK (
        (status = 'Rejected' AND rejection_reason IS NOT NULL)
        OR (status <> 'Rejected' AND rejection_reason IS NULL)
    )
);

-- 請領單列表的主要查詢型態：依狀態 + 科室 + 日期區間（FR-305）。
CREATE INDEX ix_requisitions_status_dept ON requisitions (status, department_id, created_at);


-- =============================================================================
-- 5. REQUISITION_LINES — 請領明細（MIG-1）
--    若把品項直接掛在請領單上，一張單就只能有一個品項。
--    拆出明細表是資料模型層級的決策，不是新增功能。
-- =============================================================================
CREATE TABLE requisition_lines (
    requisition_line_id     NUMBER(19)      GENERATED ALWAYS AS IDENTITY,
    requisition_id          NUMBER(19)      NOT NULL,
    line_no                 NUMBER(5)       NOT NULL,
    item_id                 NUMBER(19)      NOT NULL,
    quantity                NUMBER(10)      NOT NULL,

    CONSTRAINT pk_requisition_lines     PRIMARY KEY (requisition_line_id),
    CONSTRAINT fk_req_lines_requisition FOREIGN KEY (requisition_id) REFERENCES requisitions (requisition_id),
    CONSTRAINT fk_req_lines_item        FOREIGN KEY (item_id) REFERENCES items (item_id),
    CONSTRAINT ck_req_lines_qty         CHECK (quantity > 0),
    CONSTRAINT uq_req_lines_line_no     UNIQUE (requisition_id, line_no),
    -- 同一張單不可以有兩筆同品項 —— 否則配批要處理「同品項分兩次請領」的合併問題，
    -- 而使用者的本意幾乎都是打錯。在資料層擋掉比在程式層擋掉可靠。
    CONSTRAINT uq_req_lines_item        UNIQUE (requisition_id, item_id)
);

CREATE INDEX ix_req_lines_requisition ON requisition_lines (requisition_id);


-- =============================================================================
-- 6. ISSUE_ALLOCATIONS — 發料配批結果（FR-303 / FR-401）
--    一筆請領明細可能跨多個批次，所以這是 1:N。
--    這張表就是「這盒醫材是從哪一批發出去的」的追溯依據 —— 醫材召回時要靠它。
-- =============================================================================
CREATE TABLE issue_allocations (
    issue_allocation_id     NUMBER(19)      GENERATED ALWAYS AS IDENTITY,
    requisition_line_id     NUMBER(19)      NOT NULL,
    stock_lot_id            NUMBER(19)      NOT NULL,
    quantity                NUMBER(10)      NOT NULL,
    -- 配批當下的效期快照。批次紀錄可能被更正，但「當時發的是哪個效期」不能被改寫。
    expiry_date_at_issue    DATE            NOT NULL,
    issued_at               TIMESTAMP(6)    DEFAULT SYS_EXTRACT_UTC(SYSTIMESTAMP) NOT NULL,
    issued_by               VARCHAR2(100 CHAR)  NOT NULL,

    CONSTRAINT pk_issue_allocations      PRIMARY KEY (issue_allocation_id),
    CONSTRAINT fk_issue_alloc_line       FOREIGN KEY (requisition_line_id) REFERENCES requisition_lines (requisition_line_id),
    CONSTRAINT fk_issue_alloc_lot        FOREIGN KEY (stock_lot_id) REFERENCES stock_lots (stock_lot_id),
    CONSTRAINT ck_issue_alloc_qty        CHECK (quantity > 0),
    -- 同一筆明細不會從同一批次配兩次（FEFO 是一次取完該批可用量才換下一批）。
    CONSTRAINT uq_issue_alloc_line_lot   UNIQUE (requisition_line_id, stock_lot_id)
);

CREATE INDEX ix_issue_alloc_lot ON issue_allocations (stock_lot_id);


-- =============================================================================
-- 7. AUDIT_LOGS — 稽核軌跡（FR-403）
--    只增不改不刪。權限在 V002 以 GRANT 限制（只給 INSERT 與 SELECT）。
-- =============================================================================
CREATE TABLE audit_logs (
    audit_log_id            NUMBER(19)      GENERATED ALWAYS AS IDENTITY,
    entity_type             VARCHAR2(64 CHAR)   NOT NULL,
    entity_id               VARCHAR2(64 CHAR)   NOT NULL,
    action                  VARCHAR2(32 CHAR)   NOT NULL,
    actor                   VARCHAR2(100 CHAR)  NOT NULL,
    occurred_at             TIMESTAMP(6)    DEFAULT SYS_EXTRACT_UTC(SYSTIMESTAMP) NOT NULL,
    old_value               CLOB,
    new_value               CLOB,

    CONSTRAINT pk_audit_logs PRIMARY KEY (audit_log_id)
);

CREATE INDEX ix_audit_logs_entity ON audit_logs (entity_type, entity_id, occurred_at);
CREATE INDEX ix_audit_logs_time   ON audit_logs (occurred_at);


-- =============================================================================
-- 註解（Oracle 的 COMMENT 會存進資料字典，是給後續維運者看的）
-- =============================================================================
COMMENT ON TABLE  items                     IS '醫材品項主檔。數量不在此表，在 stock_lots。';
COMMENT ON TABLE  stock_lots                IS '庫存批次。品項+批號+效期+儲位 的唯一組合，數量掛在這裡。';
COMMENT ON COLUMN stock_lots.expiry_date    IS '有效期限。語意為「這一天（含）之前仍可使用」。';
COMMENT ON COLUMN stock_lots.row_version    IS '樂觀並發控制版本號，由應用程式在每次更新時遞增。';
COMMENT ON TABLE  requisition_lines         IS '請領明細。一張請領單可含多個品項，明細獨立成表，才能逐項配批與追溯。';
COMMENT ON TABLE  issue_allocations         IS '發料配批結果，醫材召回追溯的依據。';
COMMENT ON TABLE  audit_logs                IS '稽核軌跡，只增不改不刪。';
