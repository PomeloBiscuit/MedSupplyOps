-- =============================================================================
-- MedSupplyOps — 示範與整合測試種子資料
--
-- 這裡建立的 5 個品項與 4 個科室，其英文欄位由 V009 在新增欄位後補入。
-- 不可在 V002 先引用尚未存在的 _EN 欄位，否則全新資料庫會在版本順序中失敗。
--
-- 效期刻意以執行當日為基準：每次套用都同時有過期與可發料批次。
-- 無菌檢查手套的批次用來驗證 FEFO：GLO-FEFO-A、GLO-FEFO-B 同效期時以
-- 批號決勝；GLO-FEFO-C 效期較晚，因此「最晚到期優先」會得到不同結果。
-- =============================================================================

INSERT INTO items (
    item_code, item_name, specification, unit_of_measure,
    tracks_lot, tracks_expiry, safety_stock_qty, created_by
) VALUES (
    'MD-0001', '無菌檢查手套', '單次使用；中號', '雙',
    1, 1, 100, 'seed'
);

INSERT INTO items (
    item_code, item_name, specification, unit_of_measure,
    tracks_lot, tracks_expiry, safety_stock_qty, created_by
) VALUES (
    'MD-0002', '一次性使用注射針', '無菌；23G', '支',
    1, 1, 100, 'seed'
);

INSERT INTO items (
    item_code, item_name, specification, unit_of_measure,
    tracks_lot, tracks_expiry, safety_stock_qty, created_by
) VALUES (
    'MD-0003', '無菌輸液套', '單次使用；含流量調節器', '組',
    1, 1, 60, 'seed'
);

INSERT INTO items (
    item_code, item_name, specification, unit_of_measure,
    tracks_lot, tracks_expiry, safety_stock_qty, created_by
) VALUES (
    'MD-0004', '一次性使用無菌中心靜脈導管置入組', '單腔；成人適用', '組',
    1, 1, 10, 'seed'
);

INSERT INTO items (
    item_code, item_name, specification, unit_of_measure,
    tracks_lot, tracks_expiry, safety_stock_qty, created_by
) VALUES (
    'MD-0005', '無菌紗布敷料', '10 公分 × 10 公分；單片包裝', '片',
    1, 1, 80, 'seed'
);

INSERT INTO departments (department_code, department_name, is_active, created_by)
VALUES ('DEP-OR', '開刀房', 1, 'seed');

INSERT INTO departments (department_code, department_name, is_active, created_by)
VALUES ('DEP-MED', '內科病房', 1, 'seed');

INSERT INTO departments (department_code, department_name, is_active, created_by)
VALUES ('DEP-ER', '急診', 1, 'seed');

INSERT INTO departments (department_code, department_name, is_active, created_by)
VALUES ('DEP-ICU', '加護病房', 1, 'seed');

-- 無菌檢查手套：一批過期、同效期以批號排序、另有兩個較晚效期的批次。
INSERT INTO stock_lots (item_id, lot_number, expiry_date, quantity, storage_location, created_by)
SELECT (SELECT item_id FROM items WHERE item_code = 'MD-0001'),
       'GLO-EXPIRED-01', TRUNC(SYSDATE) - 14, 50, '中央庫房-A01', 'seed'
FROM dual;

INSERT INTO stock_lots (item_id, lot_number, expiry_date, quantity, storage_location, created_by)
SELECT (SELECT item_id FROM items WHERE item_code = 'MD-0001'),
       'GLO-FEFO-A', TRUNC(SYSDATE) + 30, 40, '中央庫房-A01', 'seed'
FROM dual;

INSERT INTO stock_lots (item_id, lot_number, expiry_date, quantity, storage_location, created_by)
SELECT (SELECT item_id FROM items WHERE item_code = 'MD-0001'),
       'GLO-FEFO-B', TRUNC(SYSDATE) + 30, 35, '中央庫房-A02', 'seed'
FROM dual;

INSERT INTO stock_lots (item_id, lot_number, expiry_date, quantity, storage_location, created_by)
SELECT (SELECT item_id FROM items WHERE item_code = 'MD-0001'),
       'GLO-FEFO-C', TRUNC(SYSDATE) + 90, 55, '中央庫房-A01', 'seed'
FROM dual;

INSERT INTO stock_lots (item_id, lot_number, expiry_date, quantity, storage_location, created_by)
SELECT (SELECT item_id FROM items WHERE item_code = 'MD-0001'),
       'GLO-FEFO-D', TRUNC(SYSDATE) + 180, 80, '中央庫房-A01', 'seed'
FROM dual;

-- 一次性使用注射針：唯一有量批次已過期，可用量必須是零。
INSERT INTO stock_lots (item_id, lot_number, expiry_date, quantity, storage_location, created_by)
SELECT (SELECT item_id FROM items WHERE item_code = 'MD-0002'),
       'NEEDLE-EXPIRED-01', TRUNC(SYSDATE) - 7, 200, '中央庫房-B01', 'seed'
FROM dual;

-- 無菌輸液套：現有量 20 低於安全存量 60；零量批次不得被配出。
INSERT INTO stock_lots (item_id, lot_number, expiry_date, quantity, storage_location, created_by)
SELECT (SELECT item_id FROM items WHERE item_code = 'MD-0003'),
       'IVSET-ZERO-01', TRUNC(SYSDATE) + 45, 0, '中央庫房-C01', 'seed'
FROM dual;

INSERT INTO stock_lots (item_id, lot_number, expiry_date, quantity, storage_location, created_by)
SELECT (SELECT item_id FROM items WHERE item_code = 'MD-0003'),
       'IVSET-LOW-01', TRUNC(SYSDATE) + 120, 20, '中央庫房-C01', 'seed'
FROM dual;

INSERT INTO stock_lots (item_id, lot_number, expiry_date, quantity, storage_location, created_by)
SELECT (SELECT item_id FROM items WHERE item_code = 'MD-0004'),
       'CVC-01', TRUNC(SYSDATE) + 75, 12, '中央庫房-D01', 'seed'
FROM dual;

INSERT INTO stock_lots (item_id, lot_number, expiry_date, quantity, storage_location, created_by)
SELECT (SELECT item_id FROM items WHERE item_code = 'MD-0005'),
       'GAUZE-01', TRUNC(SYSDATE) + 20, 30, '中央庫房-E01', 'seed'
FROM dual;

INSERT INTO stock_lots (item_id, lot_number, expiry_date, quantity, storage_location, created_by)
SELECT (SELECT item_id FROM items WHERE item_code = 'MD-0005'),
       'GAUZE-02', TRUNC(SYSDATE) + 150, 100, '中央庫房-E01', 'seed'
FROM dual;

COMMIT;
