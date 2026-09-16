-- 主檔採「原文 + 英文選填欄」而非 translations 表：本系統只需要可靠的英文覆蓋，
-- 且英文未提供時必須誠實地回退原文，不以必填或預設值製造假翻譯。
ALTER TABLE departments ADD name_en VARCHAR2(200 CHAR);

ALTER TABLE items ADD (
    item_name_en VARCHAR2(200 CHAR),
    specification_en VARCHAR2(400 CHAR),
    unit_of_measure_en VARCHAR2(20 CHAR)
);

ALTER TABLE identity_users ADD display_name_en VARCHAR2(100 CHAR);

COMMENT ON COLUMN departments.name_en IS '科室英文名稱；選填，顯示時空值回退原文。';
COMMENT ON COLUMN items.item_name_en IS '品項英文名稱；選填，顯示時空值回退原文。';
COMMENT ON COLUMN items.specification_en IS '品項英文規格；選填，顯示時空值回退原文。';
COMMENT ON COLUMN items.unit_of_measure_en IS '品項英文計量單位；選填，顯示時空值回退原文。';
COMMENT ON COLUMN identity_users.display_name_en IS '使用者英文顯示名；選填，顯示時空值回退原文。';

-- V002 建立的五個品項與四個科室在新欄位存在後補上英文；既有環境與全新建庫走同一路徑。
UPDATE items
SET item_name_en = 'Sterile Examination Gloves',
    specification_en = 'Single-use; Medium',
    unit_of_measure_en = 'Pair'
WHERE item_code = 'MD-0001';

UPDATE items
SET item_name_en = 'Single-use Hypodermic Needle',
    specification_en = 'Sterile; 23G',
    unit_of_measure_en = 'Piece'
WHERE item_code = 'MD-0002';

UPDATE items
SET item_name_en = 'Sterile Infusion Set',
    specification_en = 'Single-use; With Flow Regulator',
    unit_of_measure_en = 'Set'
WHERE item_code = 'MD-0003';

UPDATE items
SET item_name_en = 'Single-use Sterile Central Venous Catheterization Set',
    specification_en = 'Single-lumen; For Adult Use',
    unit_of_measure_en = 'Set'
WHERE item_code = 'MD-0004';

UPDATE items
SET item_name_en = 'Sterile Gauze Dressing',
    specification_en = '10 cm x 10 cm; Individually Wrapped',
    unit_of_measure_en = 'Piece'
WHERE item_code = 'MD-0005';

UPDATE departments SET name_en = 'Operating Room' WHERE department_code = 'DEP-OR';
UPDATE departments SET name_en = 'Medical Ward' WHERE department_code = 'DEP-MED';
UPDATE departments SET name_en = 'Emergency Department' WHERE department_code = 'DEP-ER';
UPDATE departments SET name_en = 'Intensive Care Unit' WHERE department_code = 'DEP-ICU';

-- 舊環境可能已由啟動 seeder 建立示範帳號；全新環境則由同一個 seeder 寫入。
UPDATE identity_users SET display_name_en = 'Xiaoming Wang' WHERE normalized_email = 'REQUESTER@EXAMPLE.LOCAL';
UPDATE identity_users SET display_name_en = 'Storekeeper Chen' WHERE normalized_email = 'KEEPER@EXAMPLE.LOCAL';
UPDATE identity_users SET display_name_en = 'Datong Lin' WHERE normalized_email = 'ADMIN@EXAMPLE.LOCAL';

COMMIT;
