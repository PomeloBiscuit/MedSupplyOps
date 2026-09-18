# 資料模型圖

`schema.mmd` 與兩張關聯綱目由 Oracle 的 `MEDSUPPLY` 資料字典產生；兩張 Chen 圖則由
`conceptual-model.json` 的元素與座標產生。產生器會檢查資料表歸屬、圖形重疊、連線穿越與畫布邊界，
讓圖面規格、實際 schema 與提交的 SVG 能在合併前比對。

先啟動本機 Oracle 容器，再從 repo 根目錄執行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/generate-er-diagram.ps1
```

提交前或在 CI 執行漂移檢查：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/generate-er-diagram.ps1 -Check
```

`-Check` 不會改寫檔案。它會重新產生 `schema.mmd`、`er-requisition.svg`、`er-identity.svg`、
`relational-schema-requisition.svg`、`relational-schema-identity.svg`，逐位元組比較提交內容，
並在不一致時指名檔案後以 exit code 1 結束。
