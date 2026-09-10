<#
.SYNOPSIS
    建立 MEDSUPPLY 的邏輯備份與 Oracle volume 冷備份。

.DESCRIPTION
    邏輯層以容器內 SYSDBA 執行 Data Pump；應用帳號不取得 DIRECTORY 或其他
    維運權限。實體層會先停止 Oracle，確認狀態為 Exited 後才封存具名 volume。
    備份寫到主機 backups/，該目錄已被 .gitignore 排除。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/backup-database.ps1
#>

[CmdletBinding()]
param(
    [string]$ContainerName = 'medsupplyops-oracle',
    [string]$VolumeName = 'medsupplyops-oracle-data',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

# 容器內執行一律走共用 helper：它用「寫檔 + docker cp」而不是 stdin 管線。
# 理由（PowerShell 5.1 會在 stdin 前面加 BOM：bash 大聲炸、sqlplus 安靜降級）
# 寫在 scripts/lib/ContainerExec.ps1 的檔頭（踩坑紀錄 L-022）。
. (Join-Path $PSScriptRoot 'lib/ContainerExec.ps1')
Set-Location $repoRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot 'backups'
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupRoot = Join-Path $OutputDirectory "database-$timestamp"
$logicalDirectory = Join-Path $backupRoot 'logical'
$physicalDirectory = Join-Path $backupRoot 'physical'
New-Item -ItemType Directory -Path $logicalDirectory, $physicalDirectory -Force | Out-Null

$dumpFile = "medsupply-$timestamp.dmp"
$exportLogFile = "medsupply-$timestamp-expdp.log"
$sqlFile = "medsupply-$timestamp.sql"
$volumeArchive = "medsupply-oracle-data-$timestamp.tar"

Write-Host "備份目錄：$backupRoot" -ForegroundColor Cyan
Write-Host '正在確認容器內 DATA_PUMP_DIR（由 SYSDBA 使用；不變更 MEDSUPPLY 權限）...' -ForegroundColor Cyan
$directorySql = @"
SET PAGESIZE 0
SET FEEDBACK OFF
SET HEADING OFF
ALTER SESSION SET CONTAINER = FREEPDB1;
SELECT directory_path FROM dba_directories WHERE directory_name = 'DATA_PUMP_DIR';
EXIT
"@
$dataPumpDirectory = (Invoke-ContainerSql -ContainerName $ContainerName -Sql $directorySql).Trim()
if ([string]::IsNullOrWhiteSpace($dataPumpDirectory)) {
    throw 'SYSDBA 找不到 DATA_PUMP_DIR，不能安全建立 Data Pump 備份。'
}

Write-Host '正在以容器內 SYSDBA 執行 schema Data Pump 匯出...' -ForegroundColor Cyan
# ORACLE_PWD 已由 docker-compose 注入容器。刻意不在主機讀 .env，也不把密碼放入主機命令列。
# Data Pump 必須收到保留的雙引號，才會把 "as sysdba" 視為登入修飾詞而非另一個引數。
# 反斜線讓 bash 把雙引號保留給 expdp，同時仍能展開容器內的 ORACLE_PWD。
$exportCommand = 'NLS_LANG=AMERICAN_AMERICA.AL32UTF8 $ORACLE_HOME/bin/expdp \"sys/$ORACLE_PWD@FREEPDB1 as sysdba\" schemas=MEDSUPPLY directory=DATA_PUMP_DIR dumpfile={0} logfile={1} metrics=Y reuse_dumpfiles=N' -f $dumpFile, $exportLogFile
$exportOutput = Invoke-ContainerBash -ContainerName $ContainerName -Command $exportCommand
$exportOutput.Trim() | Write-Host

Write-Host '正在把 .dmp 與 expdp 記錄檔複製到容器外主機路徑...' -ForegroundColor Cyan
& docker cp "${ContainerName}:$dataPumpDirectory/$dumpFile" $logicalDirectory
if ($LASTEXITCODE -ne 0) { throw '複製 .dmp 到主機失敗。' }
& docker cp "${ContainerName}:$dataPumpDirectory/$exportLogFile" $logicalDirectory
if ($LASTEXITCODE -ne 0) { throw '複製 expdp 記錄檔到主機失敗。' }

$hostDump = Join-Path $logicalDirectory $dumpFile
if (-not (Test-Path -LiteralPath $hostDump) -or (Get-Item -LiteralPath $hostDump).Length -le 0) {
    throw '容器外的 .dmp 不存在或大小為 0；停止，不進行任何破壞性操作。'
}

Write-Host '正在以 impdp SQLFILE 驗證備份內含 DDL（不會匯入資料）...' -ForegroundColor Cyan
# Oracle 26 的 SQLFILE 會將既有中文 COMMENT 走客戶端轉碼；結構驗證排除 COMMENT，
# 而真正的 impdp 還原仍完整匯入 dump 的 metadata 與資料。
$sqlFileCommand = 'NLS_LANG=AMERICAN_AMERICA.AL32UTF8 $ORACLE_HOME/bin/impdp \"sys/$ORACLE_PWD@FREEPDB1 as sysdba\" directory=DATA_PUMP_DIR dumpfile={0} sqlfile={1} exclude=COMMENT' -f $dumpFile, $sqlFile
$sqlFileOutput = Invoke-ContainerBash -ContainerName $ContainerName -Command $sqlFileCommand
$sqlFileOutput.Trim() | Write-Host
& docker cp "${ContainerName}:$dataPumpDirectory/$sqlFile" $logicalDirectory
if ($LASTEXITCODE -ne 0) { throw '複製 impdp SQLFILE 到主機失敗。' }

$hostSqlFile = Join-Path $logicalDirectory $sqlFile
$createTableCount = (Select-String -LiteralPath $hostSqlFile -Pattern '^CREATE TABLE ' -CaseSensitive).Count
$tableDataLines = Get-Content -LiteralPath (Join-Path $logicalDirectory $exportLogFile) |
    Where-Object { $_ -match 'exported "MEDSUPPLY"\.".+".*\s\d+ rows(?:\s|$)' }
if ($tableDataLines.Count -eq 0) {
    throw 'expdp 記錄檔沒有任何 MEDSUPPLY 資料列數；停止，不進行任何破壞性操作。'
}
if ($createTableCount -ne 15) {
    throw "SQLFILE 的 CREATE TABLE 數為 $createTableCount，不是預期 15；停止，不進行任何破壞性操作。"
}

Write-Host 'Data Pump 驗證通過：' -ForegroundColor Green
$tableDataLines | ForEach-Object { Write-Host "  $_" }
Write-Host "  主機檔案：$hostDump ($((Get-Item -LiteralPath $hostDump).Length) bytes)"
Write-Host "  SQLFILE CREATE TABLE：$createTableCount"

Write-Host '正在停止 Oracle，準備 cold volume backup...' -ForegroundColor Cyan
& docker compose stop oracle
if ($LASTEXITCODE -ne 0) { throw 'docker compose stop oracle 失敗。' }

try {
    # 不用 table 格式判斷：標題列本身不含 Exited，會造成假陰性。
    $containerStatus = (& docker ps -a --filter "name=^/$ContainerName$" --format '{{.Status}}').Trim()
    $containerState = "NAMES                 STATUS`n$ContainerName   $containerStatus"
    $containerState | Set-Content -LiteralPath (Join-Path $physicalDirectory 'container-state-at-copy.txt') -Encoding utf8
    $containerState | Write-Host
    if ($containerStatus -notmatch '^Exited') {
        throw 'Oracle 容器不是 Exited；拒絕建立可能不一致的熱 volume 備份。'
    }

    $resolvedPhysicalDirectory = (Resolve-Path -LiteralPath $physicalDirectory).Path
    Write-Host '正在封存停止中的具名 volume...' -ForegroundColor Cyan
    & docker run --rm --user 0:0 --entrypoint /bin/bash --mount "type=volume,source=$VolumeName,target=/source,readonly" --mount "type=bind,source=$resolvedPhysicalDirectory,target=/backup" container-registry.oracle.com/database/free:latest -lc "tar -C /source -cf /backup/$volumeArchive ."
    if ($LASTEXITCODE -ne 0) { throw 'volume tar 封存失敗。' }
}
finally {
    Write-Host '正在重新啟動 Oracle...' -ForegroundColor Cyan
    & docker compose start oracle
    if ($LASTEXITCODE -ne 0) { throw 'docker compose start oracle 失敗；請立即檢查容器。' }
}

$hostArchive = Join-Path $physicalDirectory $volumeArchive
if (-not (Test-Path -LiteralPath $hostArchive) -or (Get-Item -LiteralPath $hostArchive).Length -le 0) {
    throw '容器外的 volume tar 不存在或大小為 0。'
}

Write-Host '兩層備份完成：' -ForegroundColor Green
Write-Host "  邏輯：$hostDump"
Write-Host "  實體：$hostArchive ($((Get-Item -LiteralPath $hostArchive).Length) bytes)"
Write-Host '請先保存這兩個容器外檔案並確認 Data Pump 驗證結果，才可進行任何破壞性還原演練。' -ForegroundColor Yellow

