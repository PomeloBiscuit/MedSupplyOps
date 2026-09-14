<#
.SYNOPSIS
    量測首頁稽核清單與「已過期仍在庫」讀取路徑的實際 logical reads。

.DESCRIPTION
    所有容器 SQL 均走 ContainerExec.ps1 的無 BOM 暫存檔 + docker cp 路徑，絕不使用 stdin 管線。
    -Baseline 會造量、雙跑兩支查詢、輸出四個 ALLSTATS LAST cursor。
    -BaselineWithV007Invisible 在 V007 已存在時重現 migration 前基準，並在 finally 還原可見性。
    -Indexed 只在相同資料上套用 V007、雙跑並輸出四個 cursor；不可單獨使用。
    -CaptureIndexed 只在已套用 V007 且資料仍在時擷取調校後 cursor，可搭配 -AppendOutput 建立完整證據檔。
    -Cleanup 只清除 itest-perf 效能資料，接著應由 check-db-clean.ps1 做獨立驗證。
    不論成功或失敗，呼叫端都必須以 09_cleanup_dashboard_read_paths.sql 清除 itest-perf 資料。
#>

[CmdletBinding(DefaultParameterSetName = 'Baseline')]
param(
    [Parameter(ParameterSetName = 'Baseline', Mandatory = $true)][switch]$Baseline,
    [Parameter(ParameterSetName = 'ReplayBaseline', Mandatory = $true)][switch]$BaselineWithV007Invisible,
    [Parameter(ParameterSetName = 'Indexed', Mandatory = $true)][switch]$Indexed,
    [Parameter(ParameterSetName = 'CaptureIndexed', Mandatory = $true)][switch]$CaptureIndexed,
    [Parameter(ParameterSetName = 'Cleanup', Mandatory = $true)][switch]$Cleanup,
    [string]$ContainerName = 'medsupplyops-oracle',
    [string]$OutputFile,
    [switch]$AppendOutput
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'lib/ContainerExec.ps1')

function Invoke-PerfSqlFile {
    param([Parameter(Mandatory)][string]$Name)

    $path = Join-Path $repoRoot "db/perf/$Name"
    Write-Host "=== $Name ===" -ForegroundColor Cyan
    return Invoke-ContainerSql -ContainerName $ContainerName -Sql ([System.IO.File]::ReadAllText($path))
}

$output = [System.Collections.Generic.List[string]]::new()
if ($Cleanup) {
    $output.Add((Invoke-PerfSqlFile '09_cleanup_dashboard_read_paths.sql'))
}
elseif ($Baseline -or $BaselineWithV007Invisible) {
    if ($BaselineWithV007Invisible) {
        $output.Add((Invoke-PerfSqlFile '13_make_dashboard_feed_index_invisible.sql'))
    }

    try {
        $output.Add((Invoke-PerfSqlFile '06_seed_dashboard_read_paths.sql'))
        $output.Add((Invoke-PerfSqlFile '07_capture_dashboard_baseline_plans.sql'))
        $output.Add((Invoke-PerfSqlFile '08_display_dashboard_baseline_plans.sql'))
    }
    finally {
        if ($BaselineWithV007Invisible) {
            $output.Add((Invoke-PerfSqlFile '14_restore_dashboard_feed_index_visible.sql'))
        }
    }
}
else {
    if ($Indexed) {
        $output.Add((Invoke-PerfSqlFile '12_apply_dashboard_feed_index.sql'))
    }
    $output.Add((Invoke-PerfSqlFile '10_capture_dashboard_indexed_plans.sql'))
    $output.Add((Invoke-PerfSqlFile '11_display_dashboard_indexed_plans.sql'))
}

$rendered = $output -join [Environment]::NewLine
if ($OutputFile) {
    $resolvedOutputFile = Join-Path $repoRoot $OutputFile
    $content = if ($AppendOutput -and (Test-Path -LiteralPath $resolvedOutputFile)) {
        [System.IO.File]::ReadAllText($resolvedOutputFile) + [Environment]::NewLine + $rendered
    }
    else {
        $rendered
    }
    [System.IO.File]::WriteAllText($resolvedOutputFile, $content, [System.Text.UTF8Encoding]::new($false))
    Write-Host "完整輸出已寫入 $OutputFile" -ForegroundColor Green
}

$rendered
