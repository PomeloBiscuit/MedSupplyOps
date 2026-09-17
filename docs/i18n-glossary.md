# MedSupplyOps i18n 術語表

預設文化為 `zh-Hant`。介面目前完整支援繁體中文與 English；日本語選項會顯示但停用，避免把未完成的翻譯誤標成可用。

| 繁體中文 | English | 使用原則 |
| --- | --- | --- |
| 請領單 | requisition | 不使用 request，以免與 HTTP request 混淆 |
| 待審核 | pending approval | 尚未由庫管員或管理員完成審核 |
| 待發料 | awaiting issue | 已核准、尚未完成庫存扣帳 |
| 發料 | issue | 動詞；完成後狀態用 issued |
| 入庫 | receiving | 功能名稱；動作用 receive |
| 可用量 | availability / available quantity | 僅為即時參考，發料時由伺服器重驗 |
| 效期 | expiry date | 批次的到期日 |
| 安全存量 | safety stock | 品項主檔設定值 |
| 科室 | department | 資料內容（實際科室名稱）不翻譯 |
| 品項 | item | 資料內容（品名與規格）不翻譯 |
| 批號 | lot number | 批次識別文字不翻譯 |
| 儲藏位置 | storage location | UI 不再使用舊稱；主檔可填英文名稱，英文缺值或舊批次對不到主檔時回退原文 |
| 顯示時區 | display time zone | 只影響時間呈現，不影響業務日曆 |

資料一律以 UTC 儲存。FEFO 的「今天」與請領單號日期固定由 `Asia/Taipei` 的 `BusinessCalendar` 決定，不隨顯示時區改變。

顯示時區清單（`DisplayTimeZone.SupportedIds`，啟動時逐一以 `TimeZoneInfo.FindSystemTimeZoneById` 驗證）：
`Asia/Taipei`、`UTC`、`Asia/Tokyo`（東京 / Tokyo）、`Asia/Shanghai`（上海 / Shanghai）、
`Asia/Singapore`（新加坡 / Singapore）、`Asia/Seoul`（首爾 / Seoul）、`Europe/London`（倫敦 / London）、
`America/New_York`（紐約 / New York）、`America/Los_Angeles`（洛杉磯 / Los Angeles）。
有夏令時間的時區（倫敦、紐約、洛杉磯）選單不標固定 UTC 偏移，避免標示半年後就是錯的。
