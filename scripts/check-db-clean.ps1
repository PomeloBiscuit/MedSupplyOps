<#
.SYNOPSIS
    確認整合測試沒有把資料留在示範資料庫裡。

.DESCRIPTION
    整合測試共用同一個 Oracle 容器，每一條都會建立自己的科室／品項／批次／請領單，
    並在結束時刪掉（`created_by LIKE 'itest%'`／`actor LIKE 'itest%'` 是它們的標記）。

    identity_users 裡的 itest-* 帳號是測試夾具基礎設施，等同種子資料，刻意不列為殘留。

    問題在於：**測試失敗或被中斷時，清理不一定跑得完。**
    而留下來的資料完全不會被任何既有檢查發現 ——
    測試的斷言都限定在自己的唯一後綴或種子資料的 `MD-*` 前綴上，
    種子資料的筆數檢查也只看 `MD-*`。
    於是示範資料庫會慢慢長出一堆 `I8E54BB0BDEA` 這種品項，
    而下一個打開庫存頁的人（例如面試官）會看到它們。

    實際發生過：一次覆核時發現資料庫裡有 4 個品項、2 個科室、4 個批次、
    2 張請領單是測試殘留 —— 而當下所有測試都是綠的、五道關卡也全過。

    所以這支腳本是第六道關卡：**跑完測試之後，資料庫必須回到只剩種子資料的狀態。**

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/check-db-clean.ps1
#>

[CmdletBinding()]
param(
    [string]$ContainerName = 'medsupplyops-oracle',
    [string]$TestMarker = 'itest',

    # 加上這個參數才會真的刪除。預設只回報，不動資料 ——
    # 刪除是不可逆的，不該是預設行為。
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

# 密碼與 docker compose 共用同一份 .env，不另外要求設定 —— 兩份來源就會有漂移。
$envFile = Join-Path $repoRoot '.env'
if (-not (Test-Path $envFile)) {
    Write-Host '找不到 .env，無法連線。請先從 .env.example 複製一份。' -ForegroundColor Red
    exit 1
}

$password = $null
foreach ($line in Get-Content $envFile) {
    $trimmed = $line.Trim()
    if ($trimmed.StartsWith('APP_DB_PASSWORD=', [StringComparison]::Ordinal)) {
        $password = $trimmed.Substring('APP_DB_PASSWORD='.Length).Trim()
    }
}
if ([string]::IsNullOrWhiteSpace($password)) {
    Write-Host '.env 裡沒有 APP_DB_PASSWORD。' -ForegroundColor Red
    exit 1
}

$sql = @"
SET PAGESIZE 0
SET FEEDBACK OFF
SET HEADING OFF
SELECT 'items|' || item_code FROM items WHERE created_by LIKE '$TestMarker%'
UNION ALL SELECT 'departments|' || department_code FROM departments WHERE created_by LIKE '$TestMarker%'
UNION ALL SELECT 'stock_lots|' || lot_number FROM stock_lots WHERE created_by LIKE '$TestMarker%'
UNION ALL SELECT 'requisitions|' || requisition_no FROM requisitions WHERE created_by LIKE '$TestMarker%'
UNION ALL SELECT 'audit_logs|' || entity_type || ':' || entity_id || ':' || action FROM audit_logs WHERE actor LIKE '$TestMarker%';
EXIT
"@

# 不要對原生執行檔用 2>&1（PowerShell 5.1 會把 stderr 包成 ErrorRecord）。
$previous = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$output = ($sql | & docker exec -i $ContainerName sqlplus -s "medsupply/$password@//localhost:1521/FREEPDB1") | Out-String
$ErrorActionPreference = $previous

if ($output -match 'ORA-\d+') {
    Write-Host '查詢失敗，無法判斷資料庫是否乾淨：' -ForegroundColor Red
    ($output -split "`n" | Where-Object { $_ -match 'ORA-\d+' }) | ForEach-Object { Write-Host "  $_" }
    # ★ 「查不到」與「乾淨」是兩件事，不可以因為查詢失敗就回報通過。
    exit 1
}

$leftovers = $output -split "`n" |
    ForEach-Object { $_.Trim() } |
    Where-Object { $_ -match '^\w+\|' }

if ($leftovers.Count -eq 0) {
    Write-Host "資料庫乾淨：沒有 created_by / actor LIKE '$TestMarker%' 的殘留資料。" -ForegroundColor Green
    exit 0
}

Write-Host "整合測試在資料庫留下了 $($leftovers.Count) 筆資料（created_by / actor LIKE '$TestMarker%'）：" -ForegroundColor Red
foreach ($row in $leftovers) {
    $parts = $row -split '\|', 2
    Write-Host ("  {0,-16} {1}" -f $parts[0], $parts[1])
}
Write-Host ''
Write-Host '這通常代表某次測試失敗或被中斷，清理沒有跑完。' -ForegroundColor Yellow

if (-not $Clean) {
    Write-Host '要清掉的話加上 -Clean 參數再跑一次。' -ForegroundColor Yellow
    exit 1
}

Write-Host '正在清除（依外鍵相依順序，只刪 created_by / actor 有測試前綴的資料）...' -ForegroundColor Cyan

# ★ 刻意不用 ON DELETE CASCADE —— schema 全域禁止（見 docs/requirements.md MIG-2），
#   清理腳本也不該是唯一的例外。依外鍵順序逐一刪除。
$cleanSql = @"
SET FEEDBACK OFF
DELETE FROM audit_logs WHERE actor LIKE '$TestMarker%';
DELETE FROM issue_allocations WHERE requisition_line_id IN (
    SELECT rl.requisition_line_id FROM requisition_lines rl
    JOIN requisitions r ON r.requisition_id = rl.requisition_id
    WHERE r.created_by LIKE '$TestMarker%');
DELETE FROM requisition_lines WHERE requisition_id IN (
    SELECT requisition_id FROM requisitions WHERE created_by LIKE '$TestMarker%');
DELETE FROM requisitions WHERE created_by LIKE '$TestMarker%';
DELETE FROM issue_allocations WHERE stock_lot_id IN (
    SELECT stock_lot_id FROM stock_lots WHERE created_by LIKE '$TestMarker%');
DELETE FROM stock_lots WHERE created_by LIKE '$TestMarker%';
DELETE FROM items WHERE created_by LIKE '$TestMarker%';
DELETE FROM departments WHERE created_by LIKE '$TestMarker%';
COMMIT;
EXIT
"@

$previous = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$cleanOutput = ($cleanSql | & docker exec -i $ContainerName sqlplus -s "medsupply/$password@//localhost:1521/FREEPDB1") | Out-String
$ErrorActionPreference = $previous

if ($cleanOutput -match 'ORA-\d+') {
    Write-Host '清除失敗：' -ForegroundColor Red
    ($cleanOutput -split "`n" | Where-Object { $_ -match 'ORA-\d+' }) | ForEach-Object { Write-Host "  $_" }
    exit 1
}

# ★ 不相信「沒報錯」就當作清乾淨了 —— 回頭再查一次。
$verify = ($sql | & docker exec -i $ContainerName sqlplus -s "medsupply/$password@//localhost:1521/FREEPDB1") | Out-String
$remaining = $verify -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^\w+\|' }

if ($remaining.Count -eq 0) {
    Write-Host '清除完成，資料庫已回到只有種子資料的狀態。' -ForegroundColor Green
    exit 0
}

Write-Host "清除後仍有 $($remaining.Count) 筆殘留：" -ForegroundColor Red
$remaining | ForEach-Object { Write-Host "  $_" }
exit 1
