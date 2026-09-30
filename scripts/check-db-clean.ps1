<#
.SYNOPSIS
    確認整合測試沒有把資料留在示範資料庫裡。
.DESCRIPTION
    從資料字典推導指定 schema 中有 CREATED_BY 或 ACTOR 欄位的表；查詢失敗或沒有受檢表就拒絕宣稱乾淨。
    沒有標記欄位的表會列出，但不視為殘留；identity_users 的測試帳號是刻意保留的夾具。
    -Clean 保留人寫的刪除順序與條件。外鍵無法分辨擁有與引用關係，刪除前必須確認受檢表都有規則。
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/check-db-clean.ps1
#>

[CmdletBinding()]
param(
    [string]$ContainerName = 'medsupplyops-oracle',
    [string]$Schema = 'MEDSUPPLY',
    [string]$TestMarker = 'itest',
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot
. (Join-Path $PSScriptRoot 'lib/ContainerExec.ps1')

if ($Schema -cnotmatch '^[A-Za-z0-9_-]+$' -or $TestMarker -cnotmatch '^[A-Za-z0-9_-]+$') {
    Write-Host 'Schema 與 TestMarker 只能包含英文字母、數字、底線與連字號，無法判斷資料庫是否乾淨。' -ForegroundColor Red
    exit 1
}
$schemaName = $Schema.ToUpperInvariant()
$quotedSchema = '"' + $schemaName + '"'
# LIKE 的底線是萬用字元；標記值須視為字面前綴。
$markerPattern = $TestMarker.Replace('_', '\_') + '%'

$dictionarySql = @"
whenever sqlerror exit failure rollback
whenever oserror exit failure rollback
SET PAGESIZE 0
SET FEEDBACK OFF
SET HEADING OFF
SET LINESIZE 32767
SET TRIMSPOOL ON
ALTER SESSION SET CONTAINER=FREEPDB1;
ALTER SESSION SET CURRENT_SCHEMA=$quotedSchema;
SELECT 'DICT|' || t.table_name || '|' ||
       MAX(CASE WHEN c.column_name = 'CREATED_BY' THEN 1 ELSE 0 END) || '|' ||
       MAX(CASE WHEN c.column_name = 'ACTOR' THEN 1 ELSE 0 END)
FROM all_tables t
LEFT JOIN all_tab_columns c
  ON c.owner = t.owner AND c.table_name = t.table_name
 AND c.column_name IN ('CREATED_BY', 'ACTOR')
WHERE t.owner = '$schemaName'
  AND t.dropped = 'NO'
GROUP BY t.table_name
ORDER BY t.table_name;
EXIT
"@

try {
    $dictionaryOutput = Invoke-ContainerSql -ContainerName $ContainerName -Sql $dictionarySql
    $tables = @()
    $seenTables = @{}
    foreach ($line in ($dictionaryOutput -split "`n")) {
        $line = $line.Trim()
        if ($line.Length -eq 0) { continue }
        if ($line -cnotmatch '^DICT\|([^|]+)\|([01])\|([01])$') { throw "無法解析資料字典輸出：$line" }
        $tableName = $Matches[1]
        $hasCreatedBy = $Matches[2] -eq '1'
        $hasActor = $Matches[3] -eq '1'
        if ($tableName -cnotmatch '^[A-Z][A-Z0-9_]*$' -or $seenTables.ContainsKey($tableName)) {
            throw "資料字典中的表名無效或重複：$tableName"
        }
        $seenTables[$tableName] = $true
        $columns = @()
        if ($hasCreatedBy) { $columns += 'CREATED_BY' }
        if ($hasActor) { $columns += 'ACTOR' }
        foreach ($column in $columns) {
            if ($column -cnotmatch '^[A-Z][A-Z0-9_]*$') { throw "資料字典中的欄名無效：$column" }
        }
        $tables += [pscustomobject]@{ Name = $tableName; Columns = $columns }
    }
    $checkedTables = @($tables | Where-Object { $_.Columns.Count -gt 0 })
    $uncheckedTables = @($tables | Where-Object { $_.Columns.Count -eq 0 })
    if ($checkedTables.Count -eq 0) {
        throw "在 $schemaName 找不到任何有 CREATED_BY／ACTOR 欄位的資料表，無法判斷是否乾淨"
    }
}
catch {
    if ($_.Exception.Message -match 'ORA-01435') {
        Write-Host "在 $schemaName 找不到任何有 CREATED_BY／ACTOR 欄位的資料表，無法判斷是否乾淨（schema 不存在）。" -ForegroundColor Red
    }
    else {
        Write-Host "資料字典查詢失敗，無法判斷資料庫是否乾淨：$($_.Exception.Message)" -ForegroundColor Red
    }
    exit 1
}

Write-Host "已檢查 $($checkedTables.Count) 張：$(($checkedTables.Name) -join '、')"
$uncheckedNames = if ($uncheckedTables.Count) { $uncheckedTables.Name -join '、' } else { '無' }
Write-Host "沒有標記欄位、無法檢查：$uncheckedNames"

function Get-ResidueSummary {
    param([object[]]$CheckedTables)
    $queries = @()
    foreach ($table in $CheckedTables) {
        if ($table.Name -cnotmatch '^[A-Z][A-Z0-9_]*$') { throw "表名無效：$($table.Name)" }
        $qualifiedTable = "$quotedSchema.`"$($table.Name)`""
        $conditions = @()
        $sampleQueries = @()
        foreach ($column in $table.Columns) {
            if ($column -cnotmatch '^[A-Z][A-Z0-9_]*$') { throw "欄名無效：$column" }
            $quotedColumn = '"' + $column + '"'
            $predicate = "$quotedColumn LIKE '$markerPattern' ESCAPE '\'"
            $conditions += $predicate
            $sampleQueries += "SELECT $quotedColumn AS marker FROM $qualifiedTable WHERE $predicate"
        }
        $where = $conditions -join ' OR '
        $samples = $sampleQueries -join "`nUNION ALL`n"
        $queries += @"
SELECT 'RES|$($table.Name)|' ||
       (SELECT COUNT(*) FROM $qualifiedTable WHERE $where) || '|' ||
       NVL((SELECT LISTAGG(SUBSTR(marker, 1, 100), ', ') WITHIN GROUP (ORDER BY marker)
            FROM (SELECT DISTINCT marker FROM ($samples)
                  ORDER BY marker FETCH FIRST 5 ROWS ONLY)), '無')
FROM dual
"@
    }
    $detectionSql = @"
whenever sqlerror exit failure rollback
whenever oserror exit failure rollback
SET PAGESIZE 0
SET FEEDBACK OFF
SET HEADING OFF
SET LINESIZE 32767
SET TRIMSPOOL ON
ALTER SESSION SET CONTAINER=FREEPDB1;
ALTER SESSION SET CURRENT_SCHEMA=$quotedSchema;
$($queries -join "`nUNION ALL`n");
EXIT
"@
    $output = Invoke-ContainerSql -ContainerName $ContainerName -Sql $detectionSql
    $results = @()
    $seen = @{}
    foreach ($line in ($output -split "`n")) {
        $line = $line.Trim()
        if ($line.Length -eq 0) { continue }
        if ($line -cnotmatch '^RES\|([^|]+)\|([0-9]+)\|(.*)$') { throw "無法解析殘留查詢輸出：$line" }
        $name = $Matches[1]
        if (-not ($CheckedTables.Name -ccontains $name) -or $seen.ContainsKey($name)) {
            throw "殘留查詢回傳未知或重複的表：$name"
        }
        $seen[$name] = $true
        $results += [pscustomobject]@{ Name = $name; Count = [long]::Parse($Matches[2]); Samples = $Matches[3] }
    }
    if ($results.Count -ne $CheckedTables.Count) {
        throw "殘留查詢只回傳 $($results.Count) 張，預期 $($CheckedTables.Count) 張"
    }
    return $results
}

try { $summary = @(Get-ResidueSummary -CheckedTables $checkedTables) }
catch {
    Write-Host "查詢失敗，無法判斷資料庫是否乾淨：$($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
$leftovers = @($summary | Where-Object { $_.Count -gt 0 })
if ($leftovers.Count -eq 0) {
    Write-Host "資料庫乾淨：沒有 CREATED_BY／ACTOR 以 '$TestMarker' 開頭的殘留資料。" -ForegroundColor Green
}
else {
    Write-Host "整合測試在資料庫留下殘留資料（CREATED_BY／ACTOR 以 '$TestMarker' 開頭）：" -ForegroundColor Red
    foreach ($row in $leftovers) {
        Write-Host "  $($row.Name)：$($row.Count) 筆；標記值（前 5 個）：$($row.Samples)"
    }
}
if (-not $Clean) {
    if ($leftovers.Count -eq 0) { exit 0 }
    Write-Host '要清掉的話加上 -Clean 參數再跑一次。' -ForegroundColor Yellow
    exit 1
}

# 下列六張有人工確認的刪除規則；無標記的子表隨其父表依順序清除。
$cleanRuleTables = @('AUDIT_LOGS', 'REQUISITIONS', 'STOCK_LOTS', 'STORAGE_LOCATIONS', 'ITEMS', 'DEPARTMENTS')
$missingRules = @($checkedTables | Where-Object { $cleanRuleTables -cnotcontains $_.Name })
if ($missingRules.Count -gt 0) {
    foreach ($table in $missingRules) {
        Write-Host "資料表 $($table.Name) 有測試標記欄位，但 -Clean 沒有它的刪除規則；請依外鍵順序補上" -ForegroundColor Red
    }
    exit 1
}
if ($leftovers.Count -eq 0) { exit 0 }

Write-Host '正在清除（依外鍵相依順序，只刪 created_by / actor 有測試前綴的資料）...' -ForegroundColor Cyan
# 刻意不用 ON DELETE CASCADE；只依確認過的擁有關係與外鍵順序刪除。
$cleanSql = @"
whenever sqlerror exit failure rollback
ALTER SESSION SET CONTAINER=FREEPDB1;
ALTER SESSION SET CURRENT_SCHEMA=$quotedSchema;
SET FEEDBACK OFF
DELETE FROM audit_logs WHERE actor LIKE '$markerPattern' ESCAPE '\';
DELETE FROM issue_allocations WHERE requisition_line_id IN (
    SELECT rl.requisition_line_id FROM requisition_lines rl
    JOIN requisitions r ON r.requisition_id = rl.requisition_id
    WHERE r.created_by LIKE '$markerPattern' ESCAPE '\');
DELETE FROM requisition_lines WHERE requisition_id IN (
    SELECT requisition_id FROM requisitions WHERE created_by LIKE '$markerPattern' ESCAPE '\');
DELETE FROM requisitions WHERE created_by LIKE '$markerPattern' ESCAPE '\';
DELETE FROM issue_allocations WHERE stock_lot_id IN (
    SELECT stock_lot_id FROM stock_lots WHERE created_by LIKE '$markerPattern' ESCAPE '\');
DELETE FROM stock_lots WHERE created_by LIKE '$markerPattern' ESCAPE '\';
DELETE FROM storage_locations WHERE created_by LIKE '$markerPattern' ESCAPE '\';
DELETE FROM items WHERE created_by LIKE '$markerPattern' ESCAPE '\';
DELETE FROM departments WHERE created_by LIKE '$markerPattern' ESCAPE '\';
COMMIT;
EXIT
"@
try { Invoke-ContainerSql -ContainerName $ContainerName -Sql $cleanSql | Out-Null }
catch {
    Write-Host "清除失敗：$($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
try { $remaining = @(Get-ResidueSummary -CheckedTables $checkedTables | Where-Object { $_.Count -gt 0 }) }
catch {
    Write-Host "清除後查詢失敗，無法判斷資料庫是否乾淨：$($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
if ($remaining.Count -eq 0) {
    Write-Host '資料庫乾淨：清除完成，已回到只有種子資料的狀態。' -ForegroundColor Green
    exit 0
}
Write-Host '清除後仍有殘留：' -ForegroundColor Red
foreach ($row in $remaining) {
    Write-Host "  $($row.Name)：$($row.Count) 筆；標記值（前 5 個）：$($row.Samples)"
}
exit 1
