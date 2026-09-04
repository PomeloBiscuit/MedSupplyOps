-- =============================================================================
-- MedSupplyOps — Identity schema（身分基礎）
--
-- ASP.NET Core Identity 的標準七張表，手寫 DDL（設計裁定 D1），不是 EF Migration 產生的。
-- 命名與型別語意比照 V001：表名／欄位名小寫（Oracle 折成大寫）、
-- 字串一律 VARCHAR2(n CHAR)、每個 constraint 都有明確名稱、零 ON DELETE CASCADE。
--
-- 與 V001／V002 的表分屬不同子系統（見 MedSupplyOpsIdentityDbContext 的類別註解）：
-- Identity 的表由 Microsoft.AspNetCore.Identity.EntityFrameworkCore 的
-- IdentityDbContext<ApplicationUser> 對映，不經過 MedSupplyOpsDbContext 的稽核欄位簿記。
-- =============================================================================


-- =============================================================================
-- 1. IDENTITY_USERS — 使用者主檔（AspNetUsers 的手寫等價物）
-- =============================================================================
CREATE TABLE identity_users (
    id                       VARCHAR2(450 CHAR)  NOT NULL,
    user_name                VARCHAR2(256 CHAR),
    normalized_user_name     VARCHAR2(256 CHAR),
    email                    VARCHAR2(256 CHAR),
    normalized_email         VARCHAR2(256 CHAR),
    email_confirmed          NUMBER(1)       DEFAULT 0 NOT NULL,
    -- ASP.NET Core Identity 預設雜湊（PBKDF2 + 每帳號 salt，設計裁定 D2）產生的字串。
    password_hash            VARCHAR2(256 CHAR),
    security_stamp           VARCHAR2(100 CHAR),
    concurrency_stamp        VARCHAR2(100 CHAR),
    phone_number             VARCHAR2(32 CHAR),
    phone_number_confirmed   NUMBER(1)       DEFAULT 0 NOT NULL,
    two_factor_enabled       NUMBER(1)       DEFAULT 0 NOT NULL,
    -- 登入失敗鎖定（SEC-8）：鎖定解除時刻。存 UTC，帶時區以對映 DateTimeOffset。
    lockout_end              TIMESTAMP(6) WITH TIME ZONE,
    lockout_enabled          NUMBER(1)       DEFAULT 1 NOT NULL,
    access_failed_count      NUMBER(10)      DEFAULT 0 NOT NULL,
    -- 自訂欄位：畫面顯示用的姓名（登入帳號是 email，不適合直接顯示，見 D5）。
    display_name             VARCHAR2(100 CHAR)  NOT NULL,

    CONSTRAINT pk_identity_users PRIMARY KEY (id),
    CONSTRAINT ck_identity_users_email_confirmed CHECK (email_confirmed IN (0, 1)),
    CONSTRAINT ck_identity_users_phone_confirmed CHECK (phone_number_confirmed IN (0, 1)),
    CONSTRAINT ck_identity_users_two_factor      CHECK (two_factor_enabled IN (0, 1)),
    CONSTRAINT ck_identity_users_lockout_enabled CHECK (lockout_enabled IN (0, 1))
);

-- Identity 的 UserManager 一律用 normalized_user_name／normalized_email 比對，
-- 唯一性也建在正規化欄位上（不是 user_name／email 本身）。
CREATE UNIQUE INDEX ux_identity_users_username ON identity_users (normalized_user_name);
CREATE INDEX ix_identity_users_email ON identity_users (normalized_email);


-- =============================================================================
-- 2. IDENTITY_ROLES — 角色主檔（AspNetRoles 的手寫等價物）
-- =============================================================================
CREATE TABLE identity_roles (
    id                 VARCHAR2(450 CHAR)  NOT NULL,
    name               VARCHAR2(256 CHAR),
    normalized_name    VARCHAR2(256 CHAR),
    concurrency_stamp  VARCHAR2(100 CHAR),

    CONSTRAINT pk_identity_roles PRIMARY KEY (id)
);

CREATE UNIQUE INDEX ux_identity_roles_name ON identity_roles (normalized_name);


-- =============================================================================
-- 3. IDENTITY_USER_ROLES — 使用者與角色的多對多關聯（AspNetUserRoles 的手寫等價物）
-- =============================================================================
CREATE TABLE identity_user_roles (
    user_id   VARCHAR2(450 CHAR)  NOT NULL,
    role_id   VARCHAR2(450 CHAR)  NOT NULL,

    CONSTRAINT pk_identity_user_roles PRIMARY KEY (user_id, role_id),
    CONSTRAINT fk_identity_user_roles_user FOREIGN KEY (user_id) REFERENCES identity_users (id),
    CONSTRAINT fk_identity_user_roles_role FOREIGN KEY (role_id) REFERENCES identity_roles (id)
);

CREATE INDEX ix_identity_user_roles_role ON identity_user_roles (role_id);


-- =============================================================================
-- 4. IDENTITY_USER_CLAIMS — 使用者宣告（AspNetUserClaims 的手寫等價物）
--    本系統目前未使用，保留是為了讓 Identity 的標準模型完整，避免日後要重新補表。
-- =============================================================================
CREATE TABLE identity_user_claims (
    id           NUMBER(19)      GENERATED ALWAYS AS IDENTITY,
    user_id      VARCHAR2(450 CHAR)  NOT NULL,
    claim_type   VARCHAR2(256 CHAR),
    claim_value  VARCHAR2(1000 CHAR),

    CONSTRAINT pk_identity_user_claims PRIMARY KEY (id),
    CONSTRAINT fk_identity_user_claims_user FOREIGN KEY (user_id) REFERENCES identity_users (id)
);

CREATE INDEX ix_identity_user_claims_user ON identity_user_claims (user_id);


-- =============================================================================
-- 5. IDENTITY_USER_LOGINS — 外部登入提供者（AspNetUserLogins 的手寫等價物）
--    本系統目前未使用（沒有外部登入），保留是為了讓 Identity 的標準模型完整。
-- =============================================================================
CREATE TABLE identity_user_logins (
    login_provider          VARCHAR2(128 CHAR)  NOT NULL,
    provider_key             VARCHAR2(256 CHAR)  NOT NULL,
    provider_display_name    VARCHAR2(256 CHAR),
    user_id                  VARCHAR2(450 CHAR)  NOT NULL,

    CONSTRAINT pk_identity_user_logins PRIMARY KEY (login_provider, provider_key),
    CONSTRAINT fk_identity_user_logins_user FOREIGN KEY (user_id) REFERENCES identity_users (id)
);

CREATE INDEX ix_identity_user_logins_user ON identity_user_logins (user_id);


-- =============================================================================
-- 6. IDENTITY_USER_TOKENS — 使用者權杖（AspNetUserTokens 的手寫等價物）
--    本系統目前未使用（例如 2FA 恢復碼），保留是為了讓 Identity 的標準模型完整。
-- =============================================================================
CREATE TABLE identity_user_tokens (
    user_id         VARCHAR2(450 CHAR)  NOT NULL,
    login_provider  VARCHAR2(128 CHAR)  NOT NULL,
    name            VARCHAR2(128 CHAR)  NOT NULL,
    value           VARCHAR2(2000 CHAR),

    CONSTRAINT pk_identity_user_tokens PRIMARY KEY (user_id, login_provider, name),
    CONSTRAINT fk_identity_user_tokens_user FOREIGN KEY (user_id) REFERENCES identity_users (id)
);


-- =============================================================================
-- 7. IDENTITY_ROLE_CLAIMS — 角色宣告（AspNetRoleClaims 的手寫等價物）
--    本系統目前未使用，保留是為了讓 Identity 的標準模型完整。
-- =============================================================================
CREATE TABLE identity_role_claims (
    id           NUMBER(19)      GENERATED ALWAYS AS IDENTITY,
    role_id      VARCHAR2(450 CHAR)  NOT NULL,
    claim_type   VARCHAR2(256 CHAR),
    claim_value  VARCHAR2(1000 CHAR),

    CONSTRAINT pk_identity_role_claims PRIMARY KEY (id),
    CONSTRAINT fk_identity_role_claims_role FOREIGN KEY (role_id) REFERENCES identity_roles (id)
);

CREATE INDEX ix_identity_role_claims_role ON identity_role_claims (role_id);


-- =============================================================================
-- 註解
-- =============================================================================
COMMENT ON TABLE identity_users IS 'Identity 使用者主檔。密碼一律是 PBKDF2 雜湊，不存明文（SEC-1）。';
COMMENT ON COLUMN identity_users.lockout_end IS '登入失敗鎖定解除時刻（SEC-8）。NULL 代表目前未被鎖定。';
COMMENT ON COLUMN identity_users.display_name IS '畫面顯示用姓名，登入帳號是 email。三個示範帳號的姓名一律虛構（OUT-7）。';
COMMENT ON TABLE identity_roles IS 'Identity 角色主檔：Requester／Storekeeper／Administrator。';
COMMENT ON TABLE identity_user_roles IS '使用者與角色的多對多關聯。';
