# HL7 FHIR R4 唯讀介接

MedSupplyOps 提供一組只讀的 FHIR R4（4.0.1）端點。FHIR 模型與 JSON 由
`Hl7.Fhir.R4` 6.4.0 產生；整合測試使用相依版本完全相符的
`Firely.Fhir.Validation.R4` 3.3.1，以及 `Hl7.Fhir.Specification.Data.R4` 6.4.0
內附的 `specification.zip`，全程離線驗證。

## 端點與授權

| 方法與路徑 | 回應 | 授權 |
|---|---|---|
| `GET /fhir/metadata` | `CapabilityStatement` | 公開，供客戶端先探索能力 |
| `GET /fhir/SupplyRequest/{id}` | 單筆 `SupplyRequest` | `FhirRead` |
| `GET /fhir/SupplyRequest?identifier={system}\|{requisitionNo}` | `searchset` Bundle | `FhirRead` |
| `GET /fhir/SupplyDelivery/{id}` | 單筆 `SupplyDelivery` | `FhirRead` |
| `GET /fhir/SupplyDelivery?based-on=SupplyRequest/{id}` | `searchset` Bundle | `FhirRead` |

`FhirRead` 僅允許 `Storekeeper` 與 `Administrator`。`Requester` 一律取得 403；
未登入存取受保護端點取得 401。`metadata` 刻意加入公開端點白名單，因為標準
FHIR 客戶端會在驗證前先讀 CapabilityStatement。

所有回應使用 `application/fhir+json; charset=utf-8`。400、401、403、404 與 405
均回傳 SDK 產生的 `OperationOutcome`，不回 ProblemDetails 或登入頁。未支援的
POST／PUT／PATCH／DELETE 會得到 405，並帶 `Allow: GET`。

## SupplyRequest 對映

FHIR R4 核心定義把 `item[x]` 與 `quantity` 都定為 1..1，而且沒有可表示一張
多品項請領單的群組欄位。因此，一筆 `requisition_lines` 是一個 SupplyRequest，
`SupplyRequest.id = requisition_line_id`。一張請領單的 N 筆明細經標準 `identifier`
搜尋取得 N 個資源，不建立私有搜尋參數、群組欄位或 extension。

每筆資源帶兩個 identifier：

| 意義 | system | value |
|---|---|---|
| 請領單號 | `https://medsupplyops.example.org/fhir/sid/requisition-no` | `requisition_no` |
| 請領明細 | `https://medsupplyops.example.org/fhir/sid/requisition-line` | `requisition_line_id` |

請領數量來自 `requisition_lines.quantity`。既有 `unit_of_measure` 尚未明確綁定
UCUM，因此只填 Quantity 的顯示單位，不把內部字串誤宣告為 UCUM code。

### 狀態對映與刻意的資訊損失

| MedSupplyOps | FHIR SupplyRequest.status |
|---|---|
| `Draft` | `draft` |
| `PendingApproval` | `active` |
| `Approved` | `active` |
| `Rejected` | `cancelled` |
| `Issued` | `completed` |
| `Closed` | `completed` |

`PendingApproval` 與 `Approved` 都成為 `active`：外部系統只能知道請求仍有效，
無法再判斷「尚待核准」或「已核准待發料」。這是 FHIR 必要值集較粗造成的刻意
資訊損失；不以值集外的 `pending-approval` 假裝保留。未知資料庫狀態會丟出例外，
不會靜默改成 `unknown`。

## SupplyDelivery 與 FEFO 追溯

一筆 `issue_allocations` 是一個 SupplyDelivery，因為 `suppliedItem` 是 0..1；
同一明細若由 FEFO 拆成兩批，搜尋結果便有兩個 Delivery：

- `SupplyDelivery.id = issue_allocation_id`
- `basedOn = SupplyRequest/{requisition_line_id}`
- `status = completed`
- `suppliedItem.quantity = issue_allocations.quantity`
- `occurrenceDateTime = issue_allocations.issued_at`
- `suppliedItem.itemReference` 指向 contained `Device`
- Device 的 `lotNumber` 取 `stock_lots.lot_number`
- Device 的 `expirationDate` **取 `issue_allocations.expiry_date_at_issue`**

效期使用發料當下的快照，不回讀 `stock_lots.expiry_date` 的現值；前者回答「當時
發出去的批次標示什麼效期」，才能作為日後召回與追溯依據。批號與效期保留在標準
contained Device，不塞進 note、identifier 或私有 extension。系統不涉及病人資料，
所以 `SupplyDelivery.patient` 永遠不填，也不產生 Patient。

## CapabilityStatement

宣告的能力恰好是：

- FHIR 4.0.1，JSON
- SupplyRequest：`read`、`search-type`；搜尋參數 `identifier`（token）
- SupplyDelivery：`read`、`search-type`；搜尋參數 `based-on`（reference）

不宣告 create、update、patch、delete、history、transaction 或其他未實作互動。

## 序列化與離線驗證

產品碼先建構 `Hl7.Fhir.Model` 的 `SupplyRequest`、`SupplyDelivery`、`Device`、
`Bundle`、`CapabilityStatement` 與 `OperationOutcome` POCO，再由官方 serializer
輸出：

```csharp
public static string Serialize(Resource resource)
    => new FhirJsonSerializer().SerializeToString(resource);
```

沒有匿名物件、Dictionary、JsonObject、自訂 JSON DTO 或 System.Text.Json 的 FHIR
輸出路徑。資料查詢留在 Infrastructure，回傳一般資料列；FHIR 套件只由 Web 參考，
Domain 維持零套件相依。

驗證器以測試輸出目錄內的 `specification.zip` 建立 `ZipSource`、`CachedResolver`、
`LocalTerminologyService` 與 `Validator`，沒有外部 resolver，也不設定遠端術語服務。
每個端點產生的正常資源、Bundle entry 及錯誤 OperationOutcome 都要 0 error。

執行：

```powershell
$env:DOTNET_CLI_UI_LANGUAGE='en'
dotnet test tests/MedSupplyOps.Integration.Tests/MedSupplyOps.Integration.Tests.csproj --nologo --filter FullyQualifiedName~FhirApiTests --logger "console;verbosity=detailed"
```

2026-09-11 的實際專屬測試結果為 7/7 通過。SupplyRequest、SupplyDelivery、Bundle
與 OperationOutcome 都是 0 error／0 warning。CapabilityStatement 是 0 error／1 warning：
離線 R4 核心包無法展開 MIME type 值集所參照的 `urn:ietf:bcp:13`，因此無法對
`format=json` 作術語展開。此 warning 完整列在測試輸出；沒有為消除 warning 而連接
網路或放寬錯誤規則。

驗證器鑑別力探針確實把送入驗證器的同一個 SupplyRequest POCO 的 `quantity` 設為
null，實際得到：

```text
Error/Invalid: details=Missing required member: 'quantity';
diagnostics=ElementDefinition trace: SupplyRequest; expression=SupplyRequest
```

恢復 Quantity 後，同一資源回到 `error=0, warning=0`。

## 刻意不做

- 不提供任何寫入或交易互動。
- 不新增資料表、欄位或 migration。
- 不修改發料服務或既有交易語意。
- 不新增 Patient、病人欄位、私有 extension 或看似標準的自造狀態碼。
- 不讓 Infrastructure 或 Domain 參考 FHIR SDK。
