# 需求規格 — MedSupplyOps（醫材耗材請領與庫存管理）

> **本檔是施工的唯一依據。** 對話中冒出的新需求一律先寫進這裡（標 `[待確認]`）再施工，不憑記憶施工。
> 版本：v0.1 ｜ 建立：2026-08-24 ｜ 狀態：待業主（P）逐條確認

---

## 0. 專案定位

MedSupplyOps 是一套**醫材耗材的請領與庫存管理系統**：
科室提出請領、庫管人員審核、系統依效期先後（FEFO）配批發料，
並保留可追溯到批號的稽核軌跡。

技術棧：C# / ASP.NET Core MVC + Web API on .NET 10 / Oracle。

**為什麼選這個領域**：

1. 醫材耗材是每家醫院都有的真實系統，**且完全不涉及病人資料**
   → 不需要假裝擁有臨床領域知識，也不會踩到個資與醫療法規的灰色地帶。
2. 它的核心規則（效期發料、並發扣庫存）正好是
   **「做錯了畫面仍然完全正常」**的那一類 ——
   配錯批次時數量對、庫存扣得剛好、頁面沒有任何異狀，
   唯一的差別是發出去的醫材是不是快過期的那一批。
   這種領域才問得出「你怎麼確認它是對的」，而不只是「你做不做得出來」。
3. 規模剛好：小到能在時程內做完並逐條驗證，
   大到足以出現真正的並發、交易與授權問題。

---

## 1. 角色

| ID | 角色 | 說明 |
|----|------|------|
| ROLE-1 | 請領人（科室人員） | 建立請領單、查自己科室的單 |
| ROLE-2 | 庫管員 | 審核請領單、執行發料、盤點 |
| ROLE-3 | 系統管理員 | 使用者/品項主檔維護、查稽核軌跡 |

---

## 2. 名詞定義（避免同義詞漂移）

| 名詞 | 定義 |
|------|------|
| **品項 Item** | 醫材主檔的一個料號。**不帶數量**。 |
| **批次 StockLot** | 「某品項 + 某批號 + 某效期 + 某儲位」的一筆庫存。**數量掛在這裡**。 |
| **請領單 Requisition** | 科室提出的需求單，含多筆明細。 |
| **請領明細 RequisitionLine** | 一張單裡的一個品項與數量。 |
| **發料 Issue** | 把批次的數量扣掉、撥給科室的動作。一筆明細可能跨多個批次。 |

---

## 3. 功能需求

### 3.1 主檔
- **FR-101** 品項主檔 CRUD：料號、品名、規格、單位、是否管制效期、是否管制批號、安全庫存量；
  品名、規格、單位另有選填英文欄位，英文未提供時顯示原文，搜尋同時比對原文與英文。
- **FR-102** 科室主檔 CRUD：科室代碼、名稱、是否啟用；名稱另有選填英文欄位，英文未提供時顯示原文。
- **FR-103** 使用者管理：帳號、姓名、Email、角色、啟用狀態；顯示名稱另有選填英文欄位，
  英文未提供時顯示原文，搜尋同時比對原文與英文。

雙語主檔採「原文欄 + `_EN` 選填欄」，不建立 translations 表。語言選擇集中在
`BilingualText`：表格只顯示依文化選出的單一值；下拉選單在兩者都有時顯示「目前語言（另一語言）」供核對。
`stock_lots.storage_location` **刻意不做雙語**：它對應倉庫現場實體標示牌（例如「中央庫房-A01」），
報表若翻成另一個名稱反而會讓人無法依牌面找到位置；這是安全與現場一致性的裁定，不是漏做。

### 3.2 庫存
- **FR-201** 批次入庫：指定品項、批號、效期、數量、儲位。
- **FR-202** 庫存查詢：依品項彙總可用量，可展開看各批次的批號 / 效期 / 數量。
- **FR-203** 效期預警：可查「N 天內到期」的批次清單，N 可調。
- **FR-204** 低於安全庫存的品項清單。

### 3.3 請領流程（本專案的主軸）
- **FR-301** 建立請領單：選科室、加入多筆明細（品項 + 數量），送出後狀態為 `待審核`。
- **FR-302** 審核：庫管員可 `核准` 或 `駁回`（駁回須填原因）。
- **FR-303** 發料：核准後執行發料，系統**自動依 FEFO（先到期先出）配批**。
- **FR-304** 狀態機：`草稿 → 待審核 → 已核准 → 已發料 → 已結案`；`待審核 → 已駁回`。
  **不允許的轉換必須被拒絕並回傳明確錯誤，不可靜默忽略。**
- **FR-305** 請領單查詢：依狀態、科室、日期區間。

### 3.4 ★ 核心規則（本專案的技術重點）
- **FR-401 FEFO 配批**：發料時，同一品項有多個批次者，**一律先發效期最早的**。
  - 單一批次不足時，**跨批次依序取用**直到滿足數量。
  - 總可用量不足時，**整筆發料失敗並回滾**，不做部分發料。
  - **已過期的批次一律不得被配到**，即使那是唯一有量的批次（此時視為可用量不足）。
  - **「效期當天」仍屬可用**：有效期限 2026-08-31 表示 8/31 可用、9/1 起不可用。
    （寫錯成提前一天作廢，畫面完全正常，只是每批少用一天，沒有人會回報。）
  - **效期相同時以「批號」決勝**，不以資料庫 Id。理由：Id 是代理鍵，同一批資料匯入
    開發庫與正式庫可能拿到不同 Id，配批結果會跟著不同，而兩邊的畫面都正常。
    批號是倉管人員實際看得到、且跨環境穩定的識別。最後再以 Id 收尾確保全序。
  - 判定過期的基準日**必須由呼叫端傳入**，配批演算法內部不得讀取系統時間 ——
    否則「今天剛好沒事、明天就錯」的缺陷會躲過所有測試。
- **FR-402 並發安全**：兩個發料動作同時扣同一批次時，**不得出現超發（庫存變負數）**。
  失敗的一方必須收到明確的衝突錯誤，而不是靜默成功。
  - 應用層以 `SELECT ... FOR UPDATE WAIT n` 序列化同一品項的發料。
    選悲觀鎖而非樂觀鎖的理由：發料是短交易、高衝突，
    讓第二個人「等一下拿到正確答案」比「做完再被告知重來」合理。
  - **等鎖逾時與庫存不足必須是兩種不同的結果。** 逾時代表庫存可能夠、只是拿不到鎖，
    呼叫端應該重試；混為一談會讓使用者看到一個假的缺貨訊息。
  - **資料庫層的 `CHECK (quantity >= 0)` 是最後防線，不可省。**
    即使應用層的鎖有漏洞，超發也會被擋成 ORA-02290 而不是寫出負庫存。
    應用層負責「給出正確且友善的結果」，資料庫層負責「保證資料永遠不會錯」。
  - 一張單有多個品項時，呼叫端**必須依 item_id 遞增順序**逐一發料，否則會死結。
- **FR-403 稽核軌跡**：所有異動（建立/審核/發料/主檔修改）寫入 `AuditLog`，記錄
  誰、何時、對哪張單、做了什麼、前後值。**AuditLog 只增不改不刪。**
- **FR-404 軟刪除**：所有主檔刪除一律為軟刪除（標記 + 時間 + 操作者），**不做實體刪除**。

### 3.5 Web API
- **FR-501** 提供 REST API 覆蓋 FR-201 / FR-202 / FR-301 / FR-303，附 OpenAPI 文件。
- **FR-502** MVC 頁面與 API 共用同一組 Application 服務，**業務規則不得在兩處各寫一份**。

### 3.6 首頁
- **FR-601** 首頁依角色顯示不同的工作儀表板：庫管員／系統管理員看全院營運數字（待審核、待發料、效期預警、低於安全庫存、已過期仍在庫、最近異動），
  請領人看自己科室的數字（待審核、待發料、本月已發料）；沒有任何角色的帳號只看到提示文字，不顯示任何數字。

### 3.7 系統整合
- **FR-602** 提供 HL7 FHIR R4 的唯讀介接（`SupplyRequest` 對應請領明細、`SupplyDelivery` 對應發料配批），
  供院內其他系統查詢請領與發料狀態。**只支援 read 與 search-type，不接受任何寫入操作**。

---

## 4. 資安需求

| ID | 需求 | 為什麼這條是必要的 |
|----|------|------------------|
| **SEC-1** | 密碼以 ASP.NET Core Identity 預設雜湊（PBKDF2 + 每帳號 salt）儲存 | 無 salt 的雜湊讓相同密碼算出相同結果，一份外洩的使用者表可用彩虹表整批還原；把雜湊放在 SQL 內計算，還會讓明文密碼出現在查詢語句與資料庫稽核日誌裡 |
| **SEC-2** | 所有變更狀態的表單啟用 AntiForgery Token | 沒有 token 時，使用者只要在登入狀態下開啟一個外部網頁，就可能在不知情的情況下送出一筆請領或發料 |
| **SEC-3** | 輸出一律經 Razor 自動編碼；任何 `Html.Raw` 需個案說明理由 | 品名、儲位、駁回原因都是人輸入的欄位。未編碼就輸出，一段 `<script>` 會在**每一個看到該筆資料的人**身上執行 |
| **SEC-4** | 登入成功後重新產生 Session/Cookie 識別碼 | 登入前後沿用同一個識別碼，攻擊者可先取得一個識別碼再誘使受害者用它登入（session fixation），登入後雙方共用同一個身分 |
| **SEC-5** | 連線字串走 User Secrets（開發）／環境變數（部署），**不得進 repo** | 機敏值一旦進版本控制就**永遠留在歷史裡**，事後改密碼救不回已經被 clone 的那一份 |
| **SEC-6** | 授權以 Policy 為準，每個 Controller Action 明確標註，並採**預設拒絕** | 「預設公開、需要保護的才標註」的漏標**永遠不會被發現**：那個端點照常運作、沒有錯誤，而且只有沒登入的人才會遇到，開發者自己測不到（自己都登入著） |
| **SEC-7** | 資料存取一律參數化查詢，**包含手寫 SQL** | 本專案為了效能刻意保留手寫 SQL（NFR-2），因此參數化不能靠 ORM 兜底，必須逐處自律 |
| **SEC-8** | 登入失敗次數限制（鎖定或延遲） | 沒有限制時，弱密碼可以在可接受的時間內用純暴力猜出來 |

---

## 5. 非功能需求

- **NFR-1** 資料庫為 **Oracle Database 26ai Free**（image tag `latest`，實測版本 23.26.3；
  `localhost:1521/FREEPDB1`）。
- **NFR-2** 讀取/報表路徑使用**手寫 Oracle SQL**（Dapper），寫入路徑使用 EF Core。
  理由：JD 明列「撰寫與調整 Oracle SQL、優化資料流程與系統效能」，全用 ORM 無法展示 SQL 能力。
- **NFR-3** 至少一支查詢要有**索引最佳化的前後對照**（含實際執行計畫與邏輯讀取次數）。
  **門檻用「邏輯讀取次數 / 掃描列數」，不用執行時間**（時間受排程與 GC 干擾，無法證偽）。
- **NFR-4** 備份與還原：提供 `scripts/backup-database.ps1` / `scripts/restore-database.ps1`，並**實際做過一次完整還原演練**，
  在 [`docs/operations/backup-restore.md`](operations/backup-restore.md) 記錄演練實況
  （備份/還原前後的資料筆數與結構對照、失敗案例、判準修正過程）。
  ⚠ 目前的演練以「還原前寫入標記資料、還原後標記消失＋列數與結構回到基準」證明真的還原，
  尚未量化成一組固定的 RPO / RTO 數字，見該文件「備份還原實測」一節。
- **NFR-5** 六道關卡（`build` / `test`（含 §9 需求追溯結構性關卡）/ 格式檢查 / 鑑別力探針 / ER 圖漂移檢查 / 資料庫殘留檢查）
  皆須可在一行指令內執行。

---

## 6. 明確不做（README 會誠實標示）

| ID | 不做的項目 | 理由 |
|----|-----------|------|
| **OUT-1** | 病歷、醫囑、掛號、健保申報 | 無臨床領域知識，寧可誠實不做，也不做一個看起來對但錯的東西 |
| **OUT-2** | 對外採購 / 廠商請購 / 驗收 | 範圍控制；入庫直接以「批次入庫」表示 |
| **OUT-3** | 多院區、多倉庫調撥 | 範圍控制；儲位僅作為批次的屬性 |
| **OUT-4** | 前端 SPA（React/Vue） | 用 MVC Razor + 原生 JS，符合 JD 的「.NET MVC + JavaScript」 |
| **OUT-5** | 高可用叢集 / 即時異地備援 | 面試作品無法演示；改以「真的做過一次還原演練」取代（NFR-4） |
| **OUT-6** | 盤點差異調整流程 | 若時程有餘再補，先標為未完成 |
| **OUT-7** | ★ 任何真實醫療機構、真實病患、真實廠商的名稱或資料 | **全面禁止**。本作品會公開在 GitHub 上，出現真實院所名稱會造成「這是不是他們的內部系統」的誤解，也可能涉及商標與名譽爭議。 |

### OUT-7 的具體規則（所有程式碼、文件與提交訊息都必須遵守）

1. **預設不命名任何醫院。** 本系統的資料模型裡根本沒有「醫院」這個實體 ——
   只有科室（`DEPARTMENTS`）。所以絕大多數情況根本不需要提到任何醫院名稱。
2. 若文件或示範**非提不可**，一律寫「示範醫院」，不得使用任何真實院所名稱
   （包含但不限於醫學中心、區域醫院、地區醫院、診所）。
3. 科室名稱可以用通用名詞（開刀房、內科病房、急診、加護病房），
   那些是全世界醫院共通的職能名稱，不指向特定機構。
4. 醫材品名可以用真實的**品類**名稱（無菌手套、輸液套、紗布），
   但**不得使用特定廠商的商品名或型號**。
5. 人名一律虛構，且明顯是範例（例如「王小明」「測試庫管員」）。
6. `created_by` 之類的稽核欄位在種子資料中一律填 `seed` 或 `system`，不填人名。


---

## 7. 資料模型與架構的關鍵決策

這一節記的是**容易走上、但在這個領域會付出代價的做法**，以及本系統為什麼不那樣做。
每一條都影響資料模型或權限邊界，事後很難改。

| ID | 容易踩的做法 | 本系統的決策與理由 |
|----|-------------|------------------|
| **MIG-1** | 請領單直接掛單一品項 ID → 一張單只能一個品項 | 拆出 `RequisitionLine` 明細表。**這是資料模型層級的決策，不是附加功能**：一張單多品項是醫材請領的常態，硬壓成一單一品項會讓「同一次請領」在資料上散成好幾筆，既無法整批審核，也做不到整張單的原子發料（FR-303） |
| **MIG-2** | 用 `ON DELETE CASCADE` 把明細掛在主檔下 | 全面軟刪除 + 限制刪除，**全 schema 零 CASCADE**。醫療場域中 CASCADE 等同銷毀稽核軌跡 —— 刪一個科室會連帶刪光它所有的請領與發料紀錄，而那正是事後追責唯一的依據 |
| **MIG-3** | 有軟刪除欄位，但實際仍然硬刪 | 落實軟刪除（FR-404），並用**函數式唯一索引**讓「只有未刪除的資料需要唯一」（`ux_items_code_active`）。**有欄位不等於有機制** |
| **MIG-4** | 用數字參數的 switch 當路由（例如 `?act=100`） | MVC 慣例路由 + 具名 Action。**路由就是權限邊界**：授權以 Action 為單位標註，路由散在 switch 裡就沒有一份可以逐一檢查的清單（見 SEC-6 與端點涵蓋測試） |
| **MIG-5** | 每個實體複製貼上一整組 Add／Edit／Del／List | 共用服務與檢視樣板。複製貼上的真正代價不是行數，是**修正只會改到其中幾份** |
| **MIG-6** | 機敏檔案（設定檔、備份、憑證）不小心進版本控制 | `.gitignore` 明確排除 `.env`、`appsettings.Development.json`、`backups/`、wallet 與憑證。進了版控就**永遠留在歷史裡**，事後改密碼救不回已被 clone 的那一份（與 SEC-5 同一個理由） |

---

## 8. 待確認 `[待確認]`

- **Q-1** `[待確認]` 請領單是否需要「部分發料」？目前 FR-401 裁定為**不做部分發料**（不足即整筆失敗）。
  這在真實醫院可能不成立，但**做部分發料會讓狀態機複雜度翻倍**，時程內建議不做。
- **Q-2** `[待確認]` 效期預警的 N 天預設值？暫定 **30 天**。
- **Q-3** `[待確認]` 是否需要 Azure OpenAI 功能（JD 第 5 點）？目前排除在 v1 外，時程有餘再評估。
- **Q-4** `[待確認]` 是否要做批號管制的「例外品項」（不管制效期的耗材）？FR-101 已保留欄位，但流程尚未定義。

---

## 9. ★ 需求追溯表

> 這張表由 [`RequirementsTraceabilityTests`](../tests/MedSupplyOps.Integration.Tests/RequirementsTraceabilityTests.cs) 結構性檢查：
> 每個 §3～§5 定義的編號恰好一列；「已實作」列的驗證欄必須指向真的存在的測試方法、探針編號（`scripts/mutation-probe.ps1`）
> 或 repo 檔案路徑；「v1 不做」「延後」列的驗證欄一律 `—`；程式與測試也不得引用表外不存在的 FR 編號。
> 狀態只允許四種字串：**已實作**、**部分實作**、**v1 不做**、**延後**。
> 只做了一部分的，狀態一律是**部分實作**，實作位置欄以「部分：」開頭寫明缺什麼 —— 關卡會擋住「部分卻標成已實作」。

| 需求 | 狀態 | 實作位置 | 驗證 |
|---|---|---|---|
| **FR-101** | 部分實作 | 部分：`db/schema/V009__add_bilingual_master_data.sql`、`src/MedSupplyOps.Infrastructure/Localization/BilingualText.cs`、`src/MedSupplyOps.Web/Controllers/ItemsController.cs`、`src/MedSupplyOps.Web/Views/Items`（新增／編輯／停用、雙語欄位、原文／英文搜尋、料號正規化、停用守衛已完成；`TracksLot`／`TracksExpiry` 寫死 `true`，尚無可設定畫面） | `T1_English_item_display_silently_falls_back_then_uses_the_override`、`T2_views_cannot_select_language_from_persistence_English_properties`、`T3_item_and_user_search_match_English_columns`、`T5_disable_guards_stock_and_open_requisitions_then_allows_safe_reuse_of_code`、`T6_item_code_is_trimmed_and_uppercased_before_duplicate_check`、`T7_forged_edit_post_cannot_change_code_or_unit_but_can_change_name` |
| **FR-102** | 延後 | 未建立：無 `DepartmentsController` 或對應維護畫面；`V009__add_bilingual_master_data.sql` 已提供選填英文名稱，既有科室選單與表格可雙語顯示 | — |
| **FR-103** | 已實作 | `src/MedSupplyOps.Web/Controllers/UsersController.cs`、`src/MedSupplyOps.Web/Views/Users`、`src/MedSupplyOps.Infrastructure/Localization/BilingualText.cs`、`AuthorizationPolicies.UserManage`（清單與原文／英文搜尋、角色／狀態篩選、雙語顯示名、新增、編輯、停用／啟用、一次性顯示重設密碼；Identity、最後管理員守衛、SecurityStamp、User audit。**Email 建立後不可修改**——它同時是登入帳號與稽核軌跡裡的身分，畫面兩處已標示） | `T3_item_and_user_search_match_English_columns`、`T4_English_department_dropdown_is_bilingual_while_inventory_table_is_single_language`、`T1_last_administrator_and_self_guards_block_all_four_direct_posts`、`T2_requester_and_storekeeper_are_denied_by_every_user_management_endpoint`、`T3_disabled_user_gets_generic_login_failure_then_can_login_after_enable`、`T4_reset_password_is_one_time_invalidates_old_password_and_session_and_audits_no_secret`、`T5_requester_without_department_is_rejected_on_create_and_edit`、`Create_edit_and_filters_persist_identity_values_and_audit_role_and_department_without_password` |
| **FR-201** | 已實作 | `src/MedSupplyOps.Infrastructure/Services/StockReceivingService.cs` | `T1_receiving_uses_the_Taipei_business_date_at_the_utc_boundary`、`T2_same_lot_with_different_expiry_is_rejected_then_matching_expiry_adds_atomically`、`T3_two_concurrent_receipts_of_a_new_lot_wait_for_item_lock_then_merge_into_one_row`、`T4_item_lock_timeout_returns_explicit_result_without_writes_or_audit`、P10、P11 |
| **FR-202** | 已實作 | `src/MedSupplyOps.Infrastructure/Queries/InventoryQueries.cs`、`src/MedSupplyOps.Infrastructure/Localization/BilingualText.cs`（表格依文化顯示單一語言；`storage_location` 刻意維持現場原文） | `GetItemAvailabilityAsync_sums_usable_lots_and_keeps_all_lot_details_in_FEFO_order`、`GetAvailability_returns_camel_case_fields_and_MD_0001_values`、`T4_English_department_dropdown_is_bilingual_while_inventory_table_is_single_language` |
| **FR-203** | 已實作 | `src/MedSupplyOps.Infrastructure/Queries/InventoryQueries.cs`、`src/MedSupplyOps.Web/Controllers/InventoryController.cs`（`Expiring`） | `GetExpiringLotsAsync_returns_only_usable_lots_in_requested_window`、`GetExpiring_returns_camel_case_fields_and_usable_seed_lot_values` |
| **FR-204** | 部分實作 | 部分：`src/MedSupplyOps.Infrastructure/Queries/InventoryQueries.cs`（`GetItemsBelowSafetyStockAsync` 已實作，數字顯示於首頁營運儀表板卡片；沒有列出品項明細的獨立清單頁） | `GetItemsBelowSafetyStockAsync_counts_only_unexpired_nonzero_lots` |
| **FR-301** | 已實作 | `src/MedSupplyOps.Web/Controllers/RequisitionsController.cs`（`Create`；科室與品項下拉採雙語對照） | `Create_submit_approve_completes_full_flow`、`Duplicate_item_returns_friendly_message_and_persists_nothing`、`Empty_lines_and_zero_quantity_are_rejected` |
| **FR-302** | 已實作 | `src/MedSupplyOps.Web/Controllers/RequisitionsController.cs`（`Approve`／`Reject`） | `Create_submit_reject_completes_full_flow`、`Reject_WithoutReason_FailsAndLeavesStatusUnchanged`、P6 |
| **FR-303** | 已實作 | `src/MedSupplyOps.Infrastructure/Services/StockIssueService.cs`、`RequisitionsController.cs`（`Issue`） | `Create_submit_approve_issue_shows_allocations_and_rejects_a_duplicate_post`、`Issues_every_line_and_moves_the_requisition_to_issued`、`A_single_insufficient_line_rolls_back_the_entire_requisition`、P9 |
| **FR-304** | 已實作 | `src/MedSupplyOps.Domain/Requisitions/RequisitionStateMachine.cs` | `ExactlyFiveTransitionsAreAllowed_AndTheyAreTheExpectedOnes`、`IllegalTransition_ThrowsInsteadOfBeingSilentlyIgnored`、`ForbiddenTransition_IsRejected`、P5 |
| **FR-305** | 已實作 | `src/MedSupplyOps.Web/Controllers/RequisitionsController.cs`（`Index` 依狀態／科室／建立日期區間查詢，表格名稱依文化單語顯示） | `Keeper_dashboard_card_numbers_match_the_pages_they_link_to` |
| **FR-401** | 已實作 | `src/MedSupplyOps.Domain/Inventory/FefoAllocator.cs` | `MultipleLots_TakesEarliestExpiryFirst`、`ExpiredLot_IsNeverAllocated_EvenWhenItIsTheOnlyLotWithStock`、`LotExpiringExactlyOnAsOfDate_IsStillUsable`、`LotsWithSameExpiry_AreOrderedByLotNumberSoResultIsDeterministic`、`WhenTotalAvailableIsLess_FailsEntirelyAndAllocatesNothing`、P1、P2、P3、P4 |
| **FR-402** | 已實作 | `src/MedSupplyOps.Infrastructure/Services/StockIssueService.cs` | `Two_concurrent_issues_of_the_last_units_never_oversell`、`Concurrent_issues_deplete_exactly_the_available_quantity_and_no_more`、`Issue_lock_timeout_shows_retry_without_claiming_stock_is_insufficient`、P7 |
| **FR-403** | 已實作 | `src/MedSupplyOps.Infrastructure/Auditing`、`ItemsController.ItemAuditJson`、`UsersController.UserAuditJson`（`AuditLog` 唯讀約束＋所有寫入路徑的稽核紀錄，含 `_EN` 欄位；User Create／Update／Disable／Enable／ResetPassword 不記錄密碼或權杖） | `AuditLog_rejects_application_updates_and_deletes`、`Cross_role_flow_records_all_four_actions_with_actual_actors`、`T5_changing_only_English_item_name_is_recorded_in_audit_JSON`、`T10_item_and_receiving_actions_write_complete_audits_with_the_logged_in_actor`、`Create_edit_and_filters_persist_identity_values_and_audit_role_and_department_without_password`、`T4_reset_password_is_one_time_invalidates_old_password_and_session_and_audits_no_secret` |
| **FR-404** | 已實作 | `src/MedSupplyOps.Web/Controllers/ItemsController.cs`（`Disable`）＋函數式唯一索引 `UX_ITEMS_CODE_ACTIVE` | `T5_disable_guards_stock_and_open_requisitions_then_allows_safe_reuse_of_code`、`T6_item_code_is_trimmed_and_uppercased_before_duplicate_check` |
| **FR-501** | 部分實作 | 部分：`src/MedSupplyOps.Web/Controllers/InventoryApiController.cs`、`src/MedSupplyOps.Web/Controllers/ItemsApiController.cs`（僅涵蓋 FR-202／FR-203 查詢；未涵蓋 FR-301 建立與 FR-303 發料；沒有 OpenAPI 文件產出） | `GetAvailability_returns_camel_case_fields_and_MD_0001_values`、`GetExpiring_returns_camel_case_fields_and_usable_seed_lot_values` |
| **FR-502** | 已實作 | `src/MedSupplyOps.Infrastructure/Queries/InventoryQueries.cs`、`src/MedSupplyOps.Infrastructure/Services/StockIssueService.cs`（MVC 與 API controller 共用同一組服務／查詢類別，未各寫一份） | P8 |
| **FR-601** | 已實作 | `src/MedSupplyOps.Web/Controllers/HomeController.cs` | `Keeper_dashboard_card_numbers_match_the_pages_they_link_to`、`Requester_does_not_see_other_departments_pending_requisition_while_keepers_count_increases`、`NoRole_account_sees_the_notice_without_any_dashboard_data`、P12 |
| **FR-602** | 已實作 | `src/MedSupplyOps.Web/Controllers/FhirController.cs` | `CapabilityStatement_declares_exactly_the_working_read_only_surface`、`Fhir_authorization_and_not_found_errors_use_FHIR_HTTP_semantics`、`Based_on_search_preserves_two_FEFO_allocations_as_contained_Devices` |
| **SEC-1** | 已實作 | `src/MedSupplyOps.Web/Program.cs`（`AddIdentity`，未自訂 `PasswordHasher`，沿用 ASP.NET Core Identity 預設 PBKDF2 + 每帳號 salt） | `src/MedSupplyOps.Web/Program.cs` |
| **SEC-2** | 已實作 | `src/MedSupplyOps.Web/Controllers/ItemsController.cs`、`RequisitionsController.cs`（`[ValidateAntiForgeryToken]`） | `Approve_without_antiforgery_token_is_rejected_with_400` |
| **SEC-3** | 已實作 | Razor 預設自動編碼；全 repo 未使用 `Html.Raw` | `Inventory_page_html_encodes_item_names_and_removes_the_probe_item` |
| **SEC-4** | 已實作 | `src/MedSupplyOps.Web/Controllers/AccountController.cs`（`SignInManager.PasswordSignInAsync` 登入成功後由 ASP.NET Core Identity 簽發全新驗證 Cookie） | `src/MedSupplyOps.Web/Controllers/AccountController.cs` |
| **SEC-5** | 已實作 | `src/MedSupplyOps.Web/DevelopmentConnectionString.cs`（開發連線字串來自 gitignore 的 `.env`；正式環境走環境變數） | `.gitignore` |
| **SEC-6** | 已實作 | `src/MedSupplyOps.Web/Program.cs`（`SetFallbackPolicy` 預設拒絕，逐一 `[Authorize(Policy = ...)]`） | `Every_routed_controller_action_is_explicitly_classified`、`Deliberately_public_list_has_no_stale_entries` |
| **SEC-7** | 已實作 | `src/MedSupplyOps.Infrastructure/Queries/InventoryQueries.cs`、`DashboardQueries.cs`、`FhirQueries.cs`（Dapper `CommandDefinition` 具名參數，逐處手寫 SQL 皆參數化） | `src/MedSupplyOps.Infrastructure/Queries/InventoryQueries.cs` |
| **SEC-8** | 已實作 | `src/MedSupplyOps.Web/Program.cs`（`Lockout.MaxFailedAccessAttempts = 5`、`DefaultLockoutTimeSpan = 15 分鐘`） | `src/MedSupplyOps.Web/Program.cs` |
| **NFR-1** | 已實作 | `docker-compose.yml`（Oracle Database Free 官方映像） | `docker-compose.yml` |
| **NFR-2** | 已實作 | `src/MedSupplyOps.Infrastructure/Queries/InventoryQueries.cs`（讀取，Dapper 手寫 SQL）、`src/MedSupplyOps.Infrastructure/Persistence/MedSupplyOpsDbContext.cs`（寫入，EF Core） | `Generated_SQL_targets_uppercase_table_and_column_names` |
| **NFR-3** | 已實作 | `docs/performance/dapper-fefo.md`、`docs/performance/dashboard-read-paths.md` | `docs/performance/dapper-fefo.md`、`docs/performance/dashboard-read-paths.md` |
| **NFR-4** | 已實作 | `docs/operations/backup-restore.md`、`scripts/backup-database.ps1`、`scripts/restore-database.ps1` | `docs/operations/backup-restore.md` |
| **NFR-5** | 已實作 | `scripts/mutation-probe.ps1`、`scripts/generate-er-diagram.ps1`、`scripts/check-db-clean.ps1` | `scripts/mutation-probe.ps1` |
