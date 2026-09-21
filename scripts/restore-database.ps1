<#
.SYNOPSIS
    從 backup-database.ps1 的邏輯或實體備份還原 Oracle。

.DESCRIPTION
    預設只檢查備份與目標狀態，不會修改資料。加上 -Force 才會還原；邏輯還原
    會拒絕非空的 MEDSUPPLY schema，實體還原會拒絕已存在的 volume。這樣可讓演練
    紀錄清楚證明「還原前目標為空」，而非把既有資料誤認為還原結果。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/restore-database.ps1 -Mode Logical -BackupDirectory backups\database-20260101-120000 -Force
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BackupDirectory,

    [ValidateSet('Logical', 'Physical')]
    [string]$Mode = 'Logical',

    [switch]$Force,

    [string]$ContainerName = 'medsupplyops-oracle',
    [string]$VolumeName = 'medsupplyops-oracle-data',
    [string]$DumpFileName,
    [ValidateRange(1, 900)][int]$HealthTimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

# 容器內執行一律走共用 helper：它用「寫檔 + docker cp」而不是 stdin 管線。
# 理由（PowerShell 5.1 會在 stdin 前面加 BOM：bash 大聲炸、sqlplus 安靜降級）
# 寫在 scripts/lib/ContainerExec.ps1 的檔頭。
. (Join-Path $PSScriptRoot 'lib/ContainerExec.ps1')
Set-Location $repoRoot

function Wait-OracleHealthy {
    $deadline = (Get-Date).AddSeconds($HealthTimeoutSeconds)
    do {
        $state = (& docker inspect -f '{{.State.Status}}|{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' $ContainerName).Trim()
        if ($LASTEXITCODE -ne 0) { throw "無法讀取容器 $ContainerName 的健康狀態。" }
        Write-Host "RESTORE_HEALTHCHECK|$state"
        if ($state -eq 'running|healthy') { return }
        Start-Sleep -Seconds 5
    } while ((Get-Date) -lt $deadline)

    throw "還原後的 Oracle 在 $HealthTimeoutSeconds 秒內沒有成為 healthy（最後狀態：$state）。"
}

$resolvedBackupDirectory = (Resolve-Path -LiteralPath $BackupDirectory).Path
if ($Mode -eq 'Logical') {
    $logicalDirectory = Join-Path $resolvedBackupDirectory 'logical'
    $dump = if ([string]::IsNullOrWhiteSpace($DumpFileName)) {
        Get-ChildItem -LiteralPath $logicalDirectory -Filter '*.dmp' -File | Select-Object -First 1
    }
    else {
        Get-Item -LiteralPath (Join-Path $logicalDirectory $DumpFileName) -ErrorAction SilentlyContinue
    }
    if ($null -eq $dump -or $dump.Length -le 0) { throw '找不到有效的邏輯 .dmp 備份。' }

    $preflightSql = @"
SET PAGESIZE 0
SET FEEDBACK OFF
SET HEADING OFF
ALTER SESSION SET CONTAINER = FREEPDB1;
SELECT 'PRE_RESTORE|USER_TABLES=' || (SELECT COUNT(*) FROM dba_tables WHERE owner = 'MEDSUPPLY')
    || '|ITEMS=' || CASE WHEN (SELECT COUNT(*) FROM dba_tables WHERE owner = 'MEDSUPPLY' AND table_name = 'ITEMS') = 0 THEN 'TABLE_NOT_FOUND' ELSE 'TABLE_PRESENT' END
    || '|IDENTITY_USERS=' || CASE WHEN (SELECT COUNT(*) FROM dba_tables WHERE owner = 'MEDSUPPLY' AND table_name = 'IDENTITY_USERS') = 0 THEN 'TABLE_NOT_FOUND' ELSE 'TABLE_PRESENT' END
FROM dual;
EXIT
"@
    $preflight = Invoke-ContainerSql -ContainerName $ContainerName -Sql $preflightSql
    $preflight.Trim() | Write-Host
    if ($preflight -notmatch 'USER_TABLES=0') {
        throw '目標 MEDSUPPLY schema 不是空的；拒絕還原，避免把舊資料誤當作還原結果。'
    }

    Write-Host "邏輯備份已找到：$($dump.FullName) ($($dump.Length) bytes)" -ForegroundColor Cyan
    if (-not $Force) {
        Write-Host '演練模式完成：未修改資料庫。真的還原請明確加上 -Force。' -ForegroundColor Yellow
        exit 0
    }

    $directorySql = @"
SET PAGESIZE 0
SET FEEDBACK OFF
SET HEADING OFF
ALTER SESSION SET CONTAINER = FREEPDB1;
SELECT directory_path FROM dba_directories WHERE directory_name = 'DATA_PUMP_DIR';
EXIT
"@
    $dataPumpDirectory = (Invoke-ContainerSql -ContainerName $ContainerName -Sql $directorySql).Trim()
    if ([string]::IsNullOrWhiteSpace($dataPumpDirectory)) { throw '找不到 DATA_PUMP_DIR。' }
    & docker cp $dump.FullName "${ContainerName}:$dataPumpDirectory/$($dump.Name)"
    if ($LASTEXITCODE -ne 0) { throw '無法把 .dmp 複製到容器內 DATA_PUMP_DIR。' }

    Write-Host '正在以容器內 SYSDBA 匯入 MEDSUPPLY schema...' -ForegroundColor Cyan
    # ORACLE_PWD 已在容器內；避免主機解析無 BOM UTF-8 的 .env 或將密碼洩露到主機命令列。
    # 反斜線保留 Data Pump 所需雙引號，且讓 bash 展開容器內的 ORACLE_PWD。
    $importLogFile = "restore-$($dump.BaseName)-impdp.log"
    $importCommand = 'NLS_LANG=AMERICAN_AMERICA.AL32UTF8 $ORACLE_HOME/bin/impdp \"sys/$ORACLE_PWD@FREEPDB1 as sysdba\" schemas=MEDSUPPLY directory=DATA_PUMP_DIR dumpfile={0} logfile={1} metrics=Y' -f $dump.Name, $importLogFile
    $importOutput = Invoke-ContainerBash -ContainerName $ContainerName -Command $importCommand
    $importOutput.Trim() | Write-Host
    $hostImportLog = Join-Path $logicalDirectory $importLogFile
    & docker cp "${ContainerName}:$dataPumpDirectory/$importLogFile" $logicalDirectory
    if ($LASTEXITCODE -ne 0) { throw '無法把 impdp log 複製到容器外；拒絕把還原視為成功。' }
    $importLog = Get-Content -LiteralPath $hostImportLog -Raw
    if ($importLog -notmatch 'Job .+ successfully completed') {
        throw "impdp 結束碼為 0，但容器內 impdp log 沒有 successfully completed：$hostImportLog"
    }
    Write-Host "RESTORE_IMPORT_LOG|$hostImportLog" -ForegroundColor Green
    $postRestoreSql = @"
SET PAGESIZE 0
SET FEEDBACK OFF
SET HEADING OFF
ALTER SESSION SET CONTAINER = FREEPDB1;
SELECT 'POST_RESTORE|items=' || (SELECT COUNT(*) FROM MEDSUPPLY.items)
    || '|departments=' || (SELECT COUNT(*) FROM MEDSUPPLY.departments)
    || '|stock_lots=' || (SELECT COUNT(*) FROM MEDSUPPLY.stock_lots)
    || '|requisitions=' || (SELECT COUNT(*) FROM MEDSUPPLY.requisitions)
FROM dual;
EXIT
"@
    (Invoke-ContainerSql -ContainerName $ContainerName -Sql $postRestoreSql).Trim() | Write-Host
    Write-Host '邏輯還原完成。請以獨立查詢驗證列數、索引、約束與 IDENTITY。' -ForegroundColor Green
    exit 0
}

$physicalDirectory = Join-Path $resolvedBackupDirectory 'physical'
$archive = Get-ChildItem -LiteralPath $physicalDirectory -Filter '*.tar' -File | Select-Object -First 1
if ($null -eq $archive -or $archive.Length -le 0) { throw '找不到有效的實體 .tar 備份。' }

$archiveDirectory = (Resolve-Path -LiteralPath $physicalDirectory).Path
$archiveEntries = @(& docker run --rm --user 0:0 --entrypoint /bin/bash --mount "type=bind,source=$archiveDirectory,target=/backup,readonly" container-registry.oracle.com/database/free:latest -lc "tar -tf /backup/$($archive.Name)")
if ($LASTEXITCODE -ne 0) { throw '無法列出實體備份 tar 內容。' }
$hasExplicitOradataRoot = @($archiveEntries | Where-Object { $_ -match '^oradata/.+\.dbf$' }).Count -gt 0
$hasLegacyDataFiles = @($archiveEntries | Where-Object { $_ -match '\.dbf$' }).Count -gt 0
if (-not $hasExplicitOradataRoot -and -not $hasLegacyDataFiles) {
    throw '實體備份 tar 未包含 Oracle .dbf 資料檔；拒絕還原。'
}
Write-Host "PRE_RESTORE|ARCHIVE_LAYOUT=$(if ($hasExplicitOradataRoot) { 'oradata-root' } else { 'legacy-volume-root' })"

$volumeExists = (& docker volume ls --filter "name=^$VolumeName$" --format '{{.Name}}') -contains $VolumeName
Write-Host "PRE_RESTORE|VOLUME_EXISTS=$volumeExists"
if ($volumeExists) {
    throw '目標 volume 已存在；拒絕覆寫。請先以明確、獨立的破壞步驟刪除 volume，再重新執行本腳本。'
}

Write-Host "實體備份已找到：$($archive.FullName) ($($archive.Length) bytes)" -ForegroundColor Cyan
if (-not $Force) {
    Write-Host '演練模式完成：未建立 volume、未還原資料。真的還原請明確加上 -Force。' -ForegroundColor Yellow
    exit 0
}

Write-Host '正在建立空白目標 volume（尚不建立 Oracle 容器）...' -ForegroundColor Cyan
# compose create 會把 image 內建的 /opt/oracle/oradata 複製到新 volume；
# 必須先在沒有任何 Oracle 容器掛載的情況下解壓，才能真正驗證目標為空。
& docker volume create $VolumeName | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'docker volume create 失敗。' }

$targetEntries = & docker run --rm --user 0:0 --entrypoint /bin/bash --mount "type=volume,source=$VolumeName,target=/target" container-registry.oracle.com/database/free:latest -lc 'find /target -mindepth 1 -maxdepth 1 -printf "%f\n"'
if ($targetEntries.Count -ne 0) { throw "新建目標 volume 不是空的：$($targetEntries -join ', ')" }
Write-Host 'PRE_RESTORE|VOLUME_ENTRIES=0'

Write-Host '正在解開 cold volume 備份...' -ForegroundColor Cyan
if ($hasExplicitOradataRoot) {
    & docker run --rm --user 0:0 --entrypoint /bin/bash --mount "type=volume,source=$VolumeName,target=/target" --mount "type=bind,source=$archiveDirectory,target=/backup,readonly" container-registry.oracle.com/database/free:latest -lc "tar --strip-components=1 -C /target -xf /backup/$($archive.Name)"
}
else {
    & docker run --rm --user 0:0 --entrypoint /bin/bash --mount "type=volume,source=$VolumeName,target=/target" --mount "type=bind,source=$archiveDirectory,target=/backup,readonly" container-registry.oracle.com/database/free:latest -lc "tar -C /target -xf /backup/$($archive.Name)"
}
if ($LASTEXITCODE -ne 0) { throw 'volume tar 解壓失敗。' }

Write-Host '正在由 compose 建立已還原 volume 的 Oracle 容器...' -ForegroundColor Cyan
& docker compose create oracle
if ($LASTEXITCODE -ne 0) { throw 'docker compose create oracle 失敗。' }

Write-Host '正在啟動實體還原後的 Oracle...' -ForegroundColor Cyan
& docker compose start oracle
if ($LASTEXITCODE -ne 0) { throw 'docker compose start oracle 失敗。' }
Wait-OracleHealthy
Write-Host '實體還原完成。請等健康檢查通過後再驗證應用程式資料。' -ForegroundColor Green

