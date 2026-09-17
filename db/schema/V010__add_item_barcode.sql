-- Oracle 的一般 B-tree 唯一索引不會保存全為 NULL 的鍵：既有品項可維持 NULL，
-- 但任何實際條碼在整張 ITEMS 表內都只能出現一次（包含已停用品項）。
ALTER TABLE items ADD barcode VARCHAR2(64 CHAR);

CREATE UNIQUE INDEX ux_items_barcode ON items (barcode);

COMMENT ON COLUMN items.barcode IS '品項條碼；選填，有值時全表唯一。';

COMMIT;
