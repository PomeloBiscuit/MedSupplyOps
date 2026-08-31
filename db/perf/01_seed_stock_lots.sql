-- FEFO 查詢的一次性效能量測資料；此檔不在 schema 目錄，容器啟動時不會自動套用。
-- 100,000 筆都掛在 MD-0001，其中僅 100 筆在 2027-01-01 後仍可用，讓索引篩選度足夠高。
DELETE FROM stock_lots WHERE created_by = 'perf-fefo';

DECLARE
    v_item_id items.item_id%TYPE;
BEGIN
    SELECT item_id
    INTO v_item_id
    FROM items
    WHERE item_code = 'MD-0001';

    FOR i IN 1 .. 100000 LOOP
        INSERT INTO stock_lots (
            item_id,
            lot_number,
            expiry_date,
            quantity,
            storage_location,
            created_by
        ) VALUES (
            v_item_id,
            'PERF-' || TO_CHAR(i, 'FM000000'),
            CASE WHEN MOD(i, 1000) = 0 THEN DATE '2027-06-01' ELSE DATE '2026-01-01' END,
            10,
            'PERF-A01',
            'perf-fefo'
        );
    END LOOP;

    COMMIT;
END;
/
