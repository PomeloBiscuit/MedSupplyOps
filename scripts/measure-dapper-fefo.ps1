# 以本機 Oracle 容器量測 FEFO 查詢，輸出實際 DBMS_XPLAN 統計。
# 不印出 .env 的密碼，也不連線 localhost 以外的任何主機。
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $repoRoot '.env'
$outputFile = Join-Path $repoRoot 'docs\performance\dapper-fefo-plan-output.txt'

if (-not (Test-Path -LiteralPath $envFile)) {
    throw '找不到 repo 根目錄 .env，無法取得本機開發資料庫密碼。'
}

$passwordLine = Get-Content -LiteralPath $envFile | Where-Object {
    $_.Trim().StartsWith('APP_DB_PASSWORD=', [StringComparison]::Ordinal)
} | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($passwordLine)) {
    throw '.env 缺少 APP_DB_PASSWORD。'
}

$password = $passwordLine.Substring($passwordLine.IndexOf('=') + 1).Trim().Trim('"')
if ([string]::IsNullOrWhiteSpace($password)) {
    throw 'APP_DB_PASSWORD 不可為空白。'
}

function Invoke-LocalOracleSql {
    param(
        [Parameter(Mandatory = $true)][string]$SqlFile,
        [switch]$ExecuteAsScript)

    $connect = 'CONNECT medsupply/"{0}"@//localhost:1521/FREEPDB1' -f $password
    if ($ExecuteAsScript) {
        $containerPath = '/tmp/fefo-capture.sql'
        & docker cp $SqlFile "medsupplyops-oracle:$containerPath"
        if ($LASTEXITCODE -ne 0) {
            throw "無法複製量測 SQL 到本機 Oracle 容器：$SqlFile"
        }

        $inputLines = @($connect, "@$containerPath")
    }
    else {
        $inputLines = @($connect, 'SET SQLBLANKLINES ON') + (Get-Content -LiteralPath $SqlFile)
    }
    $result = $inputLines | & docker exec -i medsupplyops-oracle bash -lc 'sqlplus -s /nolog'
    if ($LASTEXITCODE -ne 0) {
        $result | Write-Output
        throw "sqlplus 失敗：$SqlFile"
    }

    return $result
}

function Invoke-LocalOracleSysSql {
    param([Parameter(Mandatory = $true)][string]$SqlFile)

    $result = Get-Content -LiteralPath $SqlFile | & docker exec -i medsupplyops-oracle bash -lc 'sqlplus -s / as sysdba'
    if ($LASTEXITCODE -ne 0) {
        $result | Write-Output
        throw "SYS sqlplus 失敗：$SqlFile"
    }

    return $result
}

$seedSql = Join-Path $repoRoot 'db\perf\01_seed_stock_lots.sql'
$plansSql = Join-Path $repoRoot 'db\perf\02_capture_fefo_plans.sql'
$displayPlansSql = Join-Path $repoRoot 'db\perf\05_display_fefo_plans.sql'
$cleanupSql = Join-Path $repoRoot 'db\perf\03_cleanup_stock_lots.sql'

try {
    Invoke-LocalOracleSql $seedSql | Out-Null
    $queryOutput = Invoke-LocalOracleSql $plansSql -ExecuteAsScript
    $planOutput = Invoke-LocalOracleSysSql $displayPlansSql
    $planOutput = $queryOutput + $planOutput
    $planOutput | Set-Content -LiteralPath $outputFile -Encoding utf8
    $planOutput
}
finally {
    Invoke-LocalOracleSql $cleanupSql
}
