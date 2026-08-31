# 資料庫 ER 圖

`schema.mmd` 不是手動維護的文件；它由 Oracle 的 `MEDSUPPLY` 資料字典產生。這讓圖與實際 schema 的差異能在合併前被檢查出來。

先啟動本機 Oracle 容器，再從 repo 根目錄執行：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/generate-er-diagram.ps1
```

提交前或在 CI 執行漂移檢查：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/generate-er-diagram.ps1 -Check
```

`-Check` 不會改寫檔案。它會重新從資料字典產生內容、逐位元組比較 `schema.mmd`，並在不一致時列出受影響的資料表、欄位或外鍵關係後以 exit code 1 結束。
