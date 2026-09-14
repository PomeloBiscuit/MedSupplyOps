<#
.SYNOPSIS
    建立 MEDSUPPLY 的邏輯備份與可驗證的 Oracle volume 冷備份。

.DESCRIPTION
    邏輯層以容器內 SYSDBA 執行 Data Pump。實體層先在 Oracle 內乾淨關機、
    確認容器以 exit code 0 結束，才封存具名 volume；重新啟動後必須等到
    healthcheck 為 healthy。任何失敗都會在本次備份目錄留下 UNTRUSTED.txt，
    明確宣告其中可能存在的檔案不可用於還原。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/backup-database.ps1
#>

[CmdletBinding()]
param(
    [string]$ContainerName = 'medsupplyops-oracle',
    [string]$VolumeName = 'medsupplyops-oracle-data',
    [string]$OutputDirectory,
    [ValidateRange(1, 600)][int]$StopTimeoutSeconds = 120,
    [ValidateRange(1, 900)][int]$HealthTimeoutSeconds = 300,
    [switch]$SkipCleanShutdownForTest
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'lib/ContainerExec.ps1')
Set-Location $repoRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $repoRoot 'backups' }

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupRoot = Join-Path $OutputDirectory "database-$timestamp"
$logicalDirectory = Join-Path $backupRoot 'logical'
$physicalDirectory = Join-Path $backupRoot 'physical'
New-Item -ItemType Directory -Path $logicalDirectory, $physicalDirectory -Force | Out-Null
$dumpFile = "medsupply-$timestamp.dmp"
$exportLogFile = "medsupply-$timestamp-expdp.log"
$sqlFile = "medsupply-$timestamp.sql"
$volumeArchive = "medsupply-oracle-data-$timestamp.tar"
$oracleLifecycleStarted = $false
$restartAttempted = $false

function Write-UntrustedMarker {
    param([Parameter(Mandatory)][string]$Reason)
    $markerPath = Join-Path $backupRoot 'UNTRUSTED.txt'
    @(
        '此備份目錄不可信；不得用於任何還原。'
        "失敗時間：$(Get-Date -Format 'o')"
        "失敗原因：$Reason"
        '此目錄可能含有半成品 .dmp 或 .tar；檔名與大小均不代表備份成功。'
    ) | Set-Content -LiteralPath $markerPath -Encoding utf8
    Write-Host "UNTRUSTED|$markerPath" -ForegroundColor Red
}

function Wait-OracleHealthy {
    $deadline = (Get-Date).AddSeconds($HealthTimeoutSeconds)
    do {
        $state = (& docker inspect -f '{{.State.Status}}|{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' $ContainerName).Trim()
        if ($LASTEXITCODE -ne 0) { throw "無法讀取容器 $ContainerName 的健康狀態。" }
        Write-Host "HEALTHCHECK|$state"
        if ($state -eq 'running|healthy') { return }
        Start-Sleep -Seconds 5
    } while ((Get-Date) -lt $deadline)
    throw "Oracle 在 $HealthTimeoutSeconds 秒內沒有成為 healthy（最後狀態：$state）。"
}

function Start-OracleAndWait {
    $script:restartAttempted = $true
    Write-Host '正在重新啟動 Oracle，並等待 healthy...' -ForegroundColor Cyan
    & docker compose start oracle
    if ($LASTEXITCODE -ne 0) { throw 'docker compose start oracle 失敗。' }
    Wait-OracleHealthy
    Write-Host 'Oracle 已重新啟動且 healthy。' -ForegroundColor Green
}

Write-Host "備份目錄：$backupRoot" -ForegroundColor Cyan
try {
    # Docker 對不存在的 volume mount 可能自動建立空 volume；先拒絕，避免把空 tar 當備份。
    & docker volume inspect $VolumeName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "找不到具名 volume $VolumeName；拒絕建立空的實體備份。" }

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
    if ([string]::IsNullOrWhiteSpace($dataPumpDirectory)) { throw 'SYSDBA 找不到 DATA_PUMP_DIR，不能安全建立 Data Pump 備份。' }

    Write-Host '正在以容器內 SYSDBA 執行 schema Data Pump 匯出...' -ForegroundColor Cyan
    $exportCommand = 'NLS_LANG=AMERICAN_AMERICA.AL32UTF8 $ORACLE_HOME/bin/expdp \"sys/$ORACLE_PWD@FREEPDB1 as sysdba\" schemas=MEDSUPPLY directory=DATA_PUMP_DIR dumpfile={0} logfile={1} metrics=Y reuse_dumpfiles=N' -f $dumpFile, $exportLogFile
    $exportOutput = Invoke-ContainerBash -ContainerName $ContainerName -Command $exportCommand
    $exportOutput.Trim() | Write-Host

    Write-Host '正在把 .dmp 與 expdp 記錄檔複製到容器外主機路徑...' -ForegroundColor Cyan
    & docker cp "${ContainerName}:$dataPumpDirectory/$dumpFile" $logicalDirectory
    if ($LASTEXITCODE -ne 0) { throw '複製 .dmp 到主機失敗。' }
    & docker cp "${ContainerName}:$dataPumpDirectory/$exportLogFile" $logicalDirectory
    if ($LASTEXITCODE -ne 0) { throw '複製 expdp 記錄檔到主機失敗。' }
    $hostDump = Join-Path $logicalDirectory $dumpFile
    if (-not (Test-Path -LiteralPath $hostDump) -or (Get-Item -LiteralPath $hostDump).Length -le 0) { throw '容器外的 .dmp 不存在或大小為 0；停止，不進行任何破壞性操作。' }

    Write-Host '正在以 impdp SQLFILE 驗證備份內含 DDL（不會匯入資料）...' -ForegroundColor Cyan
    $sqlFileCommand = 'NLS_LANG=AMERICAN_AMERICA.AL32UTF8 $ORACLE_HOME/bin/impdp \"sys/$ORACLE_PWD@FREEPDB1 as sysdba\" directory=DATA_PUMP_DIR dumpfile={0} sqlfile={1} exclude=COMMENT' -f $dumpFile, $sqlFile
    $sqlFileOutput = Invoke-ContainerBash -ContainerName $ContainerName -Command $sqlFileCommand
    $sqlFileOutput.Trim() | Write-Host
    & docker cp "${ContainerName}:$dataPumpDirectory/$sqlFile" $logicalDirectory
    if ($LASTEXITCODE -ne 0) { throw '複製 impdp SQLFILE 到主機失敗。' }
    $hostSqlFile = Join-Path $logicalDirectory $sqlFile
    $createTableCount = (Select-String -LiteralPath $hostSqlFile -Pattern '^CREATE TABLE ' -CaseSensitive).Count
    $tableDataLines = Get-Content -LiteralPath (Join-Path $logicalDirectory $exportLogFile) | Where-Object { $_ -match 'exported "MEDSUPPLY"\.".+".*\s\d+ rows(?:\s|$)' }
    if ($tableDataLines.Count -eq 0) { throw 'expdp 記錄檔沒有任何 MEDSUPPLY 資料列數；停止，不進行任何破壞性操作。' }
    if ($createTableCount -ne 15) { throw "SQLFILE 的 CREATE TABLE 數為 $createTableCount，不是預期 15；停止，不進行任何破壞性操作。" }
    Write-Host 'Data Pump 驗證通過：' -ForegroundColor Green
    $tableDataLines | ForEach-Object { Write-Host "  $_" }
    Write-Host "  主機檔案：$hostDump ($((Get-Item -LiteralPath $hostDump).Length) bytes)"
    Write-Host "  SQLFILE CREATE TABLE：$createTableCount"

    # shutdown immediate 成功後容器仍活著，但資料庫已關閉；任何後續失敗均須負責重新啟動。
    $oracleLifecycleStarted = $true
    $shutdownVerified = $false
    if ($SkipCleanShutdownForTest) {
        # 只供 T5 驗證 137 拒絕路徑；絕不可用於建立備份。
        $shutdownEvidencePath = Join-Path $physicalDirectory 'shutdown-output.txt'
        'SKIPPED_FOR_TEST: clean shutdown was deliberately skipped.' | Set-Content -LiteralPath $shutdownEvidencePath -Encoding utf8
        Write-Host 'T5_TEST|已暫時跳過 clean shutdown；此輪必須被拒絕。' -ForegroundColor Yellow
    }
    else {
        Write-Host '正在以 SYSDBA 執行 Oracle clean shutdown immediate...' -ForegroundColor Cyan
        $shutdownSql = @"
WHENEVER OSERROR EXIT FAILURE
WHENEVER SQLERROR EXIT SQL.SQLCODE
SHUTDOWN IMMEDIATE;
EXIT SUCCESS
"@
        $shutdownOutput = Invoke-ContainerSql -ContainerName $ContainerName -Sql $shutdownSql
        $shutdownOutput.Trim() | Write-Host
        $shutdownEvidencePath = Join-Path $physicalDirectory 'shutdown-output.txt'
        $shutdownOutput | Set-Content -LiteralPath $shutdownEvidencePath -Encoding utf8
        $shutdownMarkers = @('Database closed\.', 'Database dismounted\.', 'ORACLE instance shut down\.')
        $missingShutdownMarkers = @($shutdownMarkers | Where-Object { $shutdownOutput -notmatch $_ })
        if ($missingShutdownMarkers.Count -ne 0) {
            throw "shutdown immediate 未輸出完整成功標記：$($missingShutdownMarkers -join ', ')。"
        }
        $shutdownVerified = $true
        Write-Host 'CLEAN_SHUTDOWN|shutdown immediate completed' -ForegroundColor Green
    }
    Write-Host "正在停止 Oracle 容器（等待上限 $StopTimeoutSeconds 秒）..." -ForegroundColor Cyan
    & docker compose stop -t $StopTimeoutSeconds oracle
    if ($LASTEXITCODE -ne 0) { throw 'docker compose stop oracle 失敗。' }
    $containerStatus = (& docker inspect -f '{{.State.Status}}' $ContainerName).Trim()
    $containerExitCode = (& docker inspect -f '{{.State.ExitCode}}' $ContainerName).Trim()
    if ($LASTEXITCODE -ne 0) { throw "無法讀取容器 $ContainerName 的結束碼。" }
    $containerState = @(
        "CONTAINER=$ContainerName STATUS=$containerStatus EXIT_CODE=$containerExitCode"
        "SHUTDOWN_VERIFIED=$shutdownVerified"
        "SHUTDOWN_EVIDENCE=$shutdownEvidencePath"
    ) -join [Environment]::NewLine
    $containerState | Set-Content -LiteralPath (Join-Path $physicalDirectory 'container-state-at-copy.txt') -Encoding utf8
    $containerState | Write-Host
    if (-not $shutdownVerified) { throw '沒有已驗證的 clean shutdown；拒絕建立實體備份。' }
    if ($containerStatus -ne 'exited') { throw "Oracle 容器狀態是 $containerStatus，不是 exited；拒絕建立實體備份。" }
    if ($containerExitCode -notin @('0', '143')) {
        throw "Oracle 容器結束碼是 $containerExitCode，不是允許的 0 或 143；拒絕建立不可信的實體備份。"
    }

    $resolvedPhysicalDirectory = (Resolve-Path -LiteralPath $physicalDirectory).Path
    Write-Host '正在封存已乾淨關機的具名 volume...' -ForegroundColor Cyan
    # 封存內顯式保留 oradata/ 根目錄；還原腳本會移除這一層再寫回 named volume。
    & docker run --rm --user 0:0 --entrypoint /bin/bash --mount "type=volume,source=$VolumeName,target=/source,readonly" --mount "type=bind,source=$resolvedPhysicalDirectory,target=/backup" container-registry.oracle.com/database/free:latest -lc "tar -C /source --transform='s#^\./#oradata/#' -cf /backup/$volumeArchive ."
    if ($LASTEXITCODE -ne 0) { throw 'volume tar 封存失敗。' }
    $archiveEntries = @(& docker run --rm --user 0:0 --entrypoint /bin/bash --mount "type=bind,source=$resolvedPhysicalDirectory,target=/backup,readonly" container-registry.oracle.com/database/free:latest -lc "tar -tf /backup/$volumeArchive")
    if ($LASTEXITCODE -ne 0) { throw '無法列出 volume tar 內容。' }
    $archiveEntries | Set-Content -LiteralPath (Join-Path $physicalDirectory 'archive-contents.txt') -Encoding utf8
    $oracleDataFiles = @($archiveEntries | Where-Object { $_ -match '^oradata/.+\.dbf$' })
    if ($oracleDataFiles.Count -eq 0) { throw 'volume tar 未包含 oradata 的 Oracle .dbf 資料檔；拒絕把它當備份。' }
    Write-Host 'TAR_CONTENTS|前幾個項目：' -ForegroundColor Green
    $archiveEntries | Select-Object -First 12 | ForEach-Object { Write-Host "  $_" }
    Write-Host "TAR_DATAFILES|count=$($oracleDataFiles.Count)|first=$($oracleDataFiles[0])" -ForegroundColor Green

    Start-OracleAndWait
    $oracleLifecycleStarted = $false
    $hostArchive = Join-Path $physicalDirectory $volumeArchive
    if (-not (Test-Path -LiteralPath $hostArchive) -or (Get-Item -LiteralPath $hostArchive).Length -le 0) { throw '容器外的 volume tar 不存在或大小為 0。' }
    Write-Host '兩層備份完成：' -ForegroundColor Green
    Write-Host "  邏輯：$hostDump"
    Write-Host "  實體：$hostArchive ($((Get-Item -LiteralPath $hostArchive).Length) bytes)"
    Write-Host '請先保存這兩個容器外檔案與 archive-contents.txt，再進行任何破壞性還原演練。' -ForegroundColor Yellow
}
catch {
    $failureReason = $_.Exception.Message
    if ($oracleLifecycleStarted -and -not $restartAttempted) {
        try { Start-OracleAndWait; $oracleLifecycleStarted = $false }
        catch { $failureReason = "$failureReason`n另外，Oracle 重新啟動失敗：$($_.Exception.Message)" }
    }
    Write-UntrustedMarker -Reason $failureReason
    Write-Error "備份失敗：$failureReason"
    exit 1
}
