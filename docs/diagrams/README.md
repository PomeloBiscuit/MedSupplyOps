# 資料模型圖

`schema.mmd` 與一張全系統關聯綱目由 Oracle 的 `MEDSUPPLY` 資料字典產生；全系統與兩張模組 Chen 圖由
`conceptual-model.json` 的元素與配置產生。兩張模組圖的中文亮色版沿用原有圖面模板。
產生器會檢查資料表歸屬、圖形重疊、連線穿越與畫布邊界，
讓圖面規格、實際 schema 與提交的 SVG 能在合併前比對。

先啟動本機 Oracle 容器，再從 repo 根目錄執行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/generate-er-diagram.ps1
```

提交前或在 CI 執行漂移檢查：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/generate-er-diagram.ps1 -Check
```

`-Check` 不會改寫檔案。它會重新產生 `schema.mmd`、`er-chen-{zh,en}-{light,dark}.svg`、
`er-requisition-{zh,en}-{light,dark}.svg`、`er-identity-{zh,en}-{light,dark}.svg` 與
`relational-schema-{zh,en}-{light,dark}.svg`，統一換行後比較提交內容，
並在不一致時指名檔案後以 exit code 1 結束。
