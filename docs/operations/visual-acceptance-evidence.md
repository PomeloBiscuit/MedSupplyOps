# 其餘頁面視覺改版的驗收補證：T1、T4、T5

驗證日期：2026-09-15。使用一支未提交、單程序的整合測試，以正式 `WebApplicationFactory` DI 圖與真實登入端點取回 Razor 回應；帳號為 `itest-requester@example.local`、`itest-keeper@example.local`、`itest-admin@example.local`。所有 HTTP 回應均為 `200`。Anti-forgery token 是每次回應的暫態隨機值，以下 HTML 片段省略其值。

測試極端資料均以 `created_by = itest-t4-evidence` 建立，並在 `finally` 依外鍵順序刪除；Identity 的 `itest-*` 測試帳號是既有測試夾具，不算業務資料殘留。

## T1：各頁實際 HTML 片段

### 庫存查詢（Storekeeper）

```html
<form method="get" class="d-flex align-items-end gap-2">
  <label class="form-label mb-1" for="asOf">查詢基準日</label>
  <input class="form-control" id="asOf" name="asOf" type="date" value="2026-09-15" />
  <button class="btn btn-outline-primary" type="submit">更新</button>
</form>
<table class="table align-middle mb-0 mso-data-table">
  <thead><tr><th scope="col">料號</th><th scope="col">品名</th>
    <th scope="col">規格</th><th scope="col">單位</th>
    <th scope="col" class="text-end">可用量</th><th scope="col" class="text-end">安全存量</th></tr></thead>
  <td class="text-end">100,000</td>
  <summary class="btn btn-sm btn-outline-secondary">展開批次明細（1 批）</summary>
</table>
```

### 效期預警（Storekeeper）

```html
<label class="form-label mb-1" for="withinDays">N 天內到期</label>
<input class="form-control" id="withinDays" name="withinDays" type="number" min="0" value="2" />
<button class="btn btn-outline-primary" type="submit">查詢</button>
<h2 class="h5 mb-0" id="expiring-list-heading">近效期批次</h2>
<span class="badge mso-badge-warn">2 天內</span>
<td><span class="badge mso-badge-warn">2026-09-16</span></td>
<td class="text-end">100,000</td>
```

### 請領單列表（Administrator）

```html
<form method="get" class="card card-body mb-4">
  <label class="form-label" for="status">狀態</label>
  <select class="form-select" id="status" name="status"><option value="">全部</option>...</select>
  <label class="form-label" for="departmentId">科室</label>
  <input class="form-control" id="createdFrom" name="createdFrom" type="date" value="" />
  <button class="btn btn-outline-primary" type="submit">篩選</button>
  <a class="btn btn-outline-secondary" href="/Requisitions">清除</a>
</form>
<thead><tr><th scope="col">單號</th><th scope="col">科室</th><th scope="col">狀態</th>
  <th scope="col">建立時間（UTC）</th><th scope="col" class="text-end">明細筆數</th></tr></thead>
<a href="/Requisitions/Details/12390">REQ-T4-ZZZZZZZZZZZZZZZZZZZZZZ</a>
<span class="badge mso-badge-bad">已駁回</span>
<a class="btn btn-sm btn-outline-secondary" href="/Requisitions/Details/12390">詳情</a>
```

### 請領單詳情（Administrator）

```html
<p class="text-muted mb-0">目前狀態：<span class="badge mso-badge-warn">待審核</span></p>
<h2 class="h5" id="review-heading">審核動作</h2>
<form method="post" action="/Requisitions/Approve/12391">
  <input type="hidden" name="rowVersion" value="0" />
  <button class="btn btn-success" type="submit">核准</button>
</form>
<label class="form-label" for="rejectionReason">駁回原因</label>
<textarea class="form-control" id="rejectionReason" name="rejectionReason" maxlength="500" required></textarea>
<button class="btn btn-danger" type="submit">駁回</button>
```

### 建立請領單（Requester）

```html
<form method="post" id="requisition-create-form" data-api-template="/api/items/{itemId}/availability">
  <select class="form-select" id="DepartmentId" name="DepartmentId">...</select>
  <span>請領明細</span>
  <button class="btn btn-sm btn-outline-primary" type="button" id="add-requisition-line">新增明細</button>
  <thead><tr><th scope="col">品項</th><th scope="col" style="width: 10rem;">數量</th>
    <th scope="col">即時可用量／最早效期（基準日 2026-09-15）</th></tr></thead>
  <button class="btn btn-primary" type="submit">送出請領單</button>
</form>
```

### 品項列表與編輯（Administrator）

```html
<h2 class="h5 mb-0" id="item-list-heading">品項主檔</h2>
<thead><tr><th scope="col">料號</th><th scope="col">名稱</th><th scope="col">規格</th>
  <th scope="col">單位</th><th scope="col" class="text-end">安全存量</th></tr></thead>
<td class="text-break mso-long-text">極端品名-名名名名名名名名名名...</td>
<a class="btn btn-sm btn-outline-primary" href="/Items/Edit/9729">編輯</a>
<button class="btn btn-sm btn-outline-danger" type="submit">停用</button>

<input class="form-control" maxlength="200" type="text" id="Name" name="Name" value="極端品名-名名名名名名名名名名..." />
<input class="form-control" type="number" min="0" max="1000000" step="1" id="SafetyStockQty" name="SafetyStockQty" value="0" />
<button class="btn btn-primary" type="submit">儲存</button>
```

### 入庫（Administrator）

```html
<label class="form-label" for="LotNumber">批號</label>
<input class="form-control" maxlength="64" autocomplete="off" type="text" id="LotNumber" name="LotNumber" value="" />
<input class="form-control" type="number" min="1" max="100000" step="1"
  data-val-range="數量必須是 1 到 100,000 的整數。" id="Quantity" name="Quantity" value="1" />
<button class="btn btn-primary" type="submit">確認入庫</button>
```

### 登入（Anonymous）

```html
<h1 class="h2 mb-1" id="login-heading">登入</h1>
<label class="form-label" for="Email">Email</label>
<input class="form-control" autocomplete="username" type="email" id="Email" name="Email" value="" />
<label class="form-label" for="Password">Password</label>
<input class="form-control" autocomplete="current-password" type="password" id="Password" name="Password" />
<button class="btn btn-primary" type="submit">登入</button>
<a class="btn btn-outline-secondary" href="/Account/Register">還沒有帳號？註冊</a>
```

## T4：極端資料與空狀態

| 輸入 | 實際 HTML 證據 | 處理結果 |
|---|---|---|
| 品名 200 字元 | `<td class="text-break mso-long-text">極端品名-名名名名名名名名名名...</td>` | `mso-long-text` 的 `overflow-wrap:anywhere` 加上 `text-break` 會換行；不截斷內容。編輯欄位亦輸出 `maxlength="200"` 與完整的 200 字元 value。 |
| 單號 29 字元 | `<a href="/Requisitions/Details/12390">REQ-T4-ZZZZZZZZZZZZZZZZZZZZ</a>` | 桌面寬度可完整顯示；在 `max-width:991.98px`，父層 `.mso-table-scroll` 水平捲動、表格最低 48rem，避免擠壓欄位。 |
| 駁回原因 500 字元 | `<dd class="col-sm-9 mso-long-text">駁駁駁駁駁駁駁駁駁駁駁駁駁駁駁駁...</dd>` | 實測 DOM 為 500 個「駁」，容器的 `mso-long-text` 以 `overflow-wrap:anywhere` 換行；不截斷。審核 textarea 也確實輸出 `maxlength="500"`。 |
| 數量 6 位數 | `<td class="text-end">100,000</td>`（庫存／效期）；`<td class="text-end">100000 盒</td>`（請領詳情） | 數字欄靠右。清單依文化格式顯示千分位；詳情保留業務數量。入庫 UI 上限實際輸出為 `max="100000"`。 |

空狀態全部由實際 MVC 回應產生，而非讀 View 原始碼或 mock：

```html
<!-- /Inventory?asOf=2026-09-15：暫時隱藏全部品項後立即還原 -->
<td colspan="6" class="mso-empty-state text-center">目前沒有可顯示的庫存品項。</td>

<!-- /Inventory/Expiring?withinDays=0&asOf=1900-01-01 -->
<td colspan="6" class="mso-empty-state text-center">此條件下沒有仍有數量的近效期批次。</td>

<!-- /Requisitions?createdFrom=1900-01-01&createdTo=1900-01-01 -->
<td colspan="6" class="mso-empty-state text-center">沒有符合條件的請領單。</td>

<!-- /Items：暫時隱藏全部品項後立即還原 -->
<td colspan="6" class="mso-empty-state text-center">目前沒有可管理的品項。</td>
```

`mso-empty-state` 使用置中、加大的內距及淡色表面；沒有留下空白表格。極端資料與空狀態均不構成版面缺陷，**本次未改任何 View**。

## T5：WCAG 2.1 文字對比度

依 WCAG 相對亮度公式計算；一般文字的 AA 門檻為 4.50:1。下列均是這次新增／調整之樣式在實際背景上的組合。

| 文字／背景 | 對比 | AA |
|---|---:|---|
| 導覽品牌 `#FFFFFF`／`#12304A` | 13.57:1 | 通過 |
| 導覽連結（78% 白合成 `#CCD3D9`）／`#12304A` | 8.97:1 | 通過 |
| 本文 `#2B3440`／頁面 `#F4F6F8` | 11.62:1 | 通過 |
| 標題 `#10171F`／白 | 18.04:1 | 通過 |
| 輔助文字 `#5B6775`／白；／頁面 | 5.77:1；5.32:1 | 通過 |
| 連結、主要 outline `#1B5E9C`／白；／頁面 | 6.71:1；6.20:1 | 通過 |
| 主要 outline hover 白／`#123F6B` | 10.77:1 | 通過 |
| 次要 outline `#495057`／白；／頁面 | 8.18:1；7.55:1 | 通過 |
| 危險 outline `#8F1D17`／白；／頁面 | 8.91:1；8.23:1 | 通過 |
| 成功標籤 `#146B41`／`#E3F3EA` | 5.69:1 | 通過 |
| 警告標籤 `#7A5200`／`#FDF3D7` | 6.25:1 | 通過 |
| 錯誤標籤 `#8F1D17`／`#FDECEA` | 7.79:1 | 通過 |
| 中性標籤 `#5B6775`／`#EEF1F4` | 5.09:1 | 通過 |
| 表格表頭／空狀態 `#5B6775`／`#F2F4F7` | 5.23:1 | 通過 |
| 危險列本文 `#2B3440`／`#FDECEA` | 11.01:1 | 通過 |

初測發現 Bootstrap 預設 outline primary `#0D6EFD`／頁面僅 4.15:1、outline secondary `#6C757D`／頁面僅 4.33:1，均未達 AA。因此本次只在 `site.css` 將 outline primary、secondary、danger 的語意色改為上述深色票；未改 View、未調整版面。
