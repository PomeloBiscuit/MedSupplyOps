-- DISPLAY_CURSOR 需要查閱動態效能檢視；量測後由 04_revoke_plan_statistics.sql 立即撤銷。
-- Oracle 23ai 的 DBMS_XPLAN 會在執行期讀多個 V$ 檢視，使用暫時的系統權限避免漏授某個內部檢視。
ALTER SESSION SET CONTAINER = FREEPDB1;

GRANT SELECT ANY DICTIONARY TO medsupply;
