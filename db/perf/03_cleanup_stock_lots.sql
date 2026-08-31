-- 只刪除 01_seed_stock_lots.sql 標記的量測資料，V002 的 11 筆示範資料不會受影響。
ALTER INDEX ix_stock_lots_fefo VISIBLE;

DELETE FROM stock_lots WHERE created_by = 'perf-fefo';
COMMIT;

SELECT COUNT(*) AS stock_lots_count FROM stock_lots;
