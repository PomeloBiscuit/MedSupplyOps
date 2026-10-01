-- 示範品項預設條碼：前四筆為虛構的內部 GTIN 號段，最後一筆為英數 Code 128 B 字元集示範。
-- 只補空白值，保留使用者在主檔中調整過的條碼。
UPDATE items SET barcode = '02000000000015' WHERE item_code = 'MD-0001' AND barcode IS NULL;
UPDATE items SET barcode = '02000000000022' WHERE item_code = 'MD-0002' AND barcode IS NULL;
UPDATE items SET barcode = '02000000000039' WHERE item_code = 'MD-0003' AND barcode IS NULL;
UPDATE items SET barcode = '02000000000046' WHERE item_code = 'MD-0004' AND barcode IS NULL;
UPDATE items SET barcode = 'MSO-B128-05' WHERE item_code = 'MD-0005' AND barcode IS NULL;

COMMIT;
