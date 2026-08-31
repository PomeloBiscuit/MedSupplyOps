<#
.SYNOPSIS
    鑑別力探針：把實作故意改壞一處，確認測試會紅，再還原。

.DESCRIPTION
    「不能區分修前與修後的驗證，等於沒有驗證。」

    測試全綠只代表「測試沒有失敗」，不代表「測試測得到那件事」。
    本腳本對每一條規則做一次可控的破壞，並記錄有幾條測試因此變紅。
    某條探針改壞了卻仍然全綠 —— 那代表對應的測試是空的，必須修測試而不是慶祝。

    兩個結構上的防呆（都是踩過坑才加的）：

    1. 改壞之後、跑測試之前，先用字串比對確認改動真的寫進檔案了。
       曾經發生過替換字串沒對上、檔案根本沒被改、測試當然全綠，
       差點據此判定「測試沒有鑑別力」。

    2. 還原時用「寫回原始位元組」而不是搬回備份檔。
       搬回備份檔會把檔案的修改時間還原成備份當下，比已編譯的 dll 還舊，
       MSBuild 會判定不需重編 —— 於是還原後跑的測試用的仍是帶著改壞的舊 dll。
       Write 會把時間戳更新為現在，強制重編。

.EXAMPLE
    pwsh -File scripts/mutation-probe.ps1
#>

[CmdletBinding()]
param(
    [string]$TestProject = 'tests/MedSupplyOps.Domain.Tests'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$domain = 'src/MedSupplyOps.Domain'

# 每一條探針對應需求文件裡的一條規則。
# Verify 是「改壞之後檔案裡應該找得到的字串」，用來證明探針真的打進去了。
$probes = @(
    @{
        Name   = 'P1  FEFO 排序反轉（改成先發最晚到期）'
        Rule   = 'FR-401 先到期先出'
        File   = "$domain/Inventory/FefoAllocator.cs"
        From   = '.OrderBy(lot => lot.ExpiryDate)'
        To     = '.OrderByDescending(lot => lot.ExpiryDate)'
        Verify = 'OrderByDescending(lot => lot.ExpiryDate)'
    },
    @{
        Name   = 'P2  過期判定差一天（< 改成 <=）'
        Rule   = 'FR-401 效期當天仍可用'
        File   = "$domain/Inventory/StockLot.cs"
        From   = 'ExpiryDate < asOf'
        To     = 'ExpiryDate <= asOf'
        Verify = 'ExpiryDate <= asOf'
    },
    @{
        Name   = 'P3  移除同效期的批號決勝鍵'
        Rule   = 'FR-401 跨環境配批一致'
        File   = "$domain/Inventory/FefoAllocator.cs"
        From   = '.ThenBy(lot => lot.LotNumber, StringComparer.Ordinal)'
        To     = ''
        Verify = '// 最後以 Id 收尾'
    },
    @{
        Name   = 'P4  允許部分發料（拿掉「不足即整筆失敗」的守衛）'
        Rule   = 'FR-401 不做部分發料'
        File   = "$domain/Inventory/FefoAllocator.cs"
        From   = 'return AllocationResult.InsufficientStock(requestedQuantity, availableCapped);'
        To     = 'requestedQuantity = availableCapped;'
        Verify = 'requestedQuantity = availableCapped;'
    },
    @{
        Name   = 'P5  狀態機偷開一條非法轉換（草稿可直接發料）'
        Rule   = 'FR-304 非法轉換須被拒絕'
        File   = "$domain/Requisitions/RequisitionStateMachine.cs"
        From   = '[(RequisitionStatus.Issued, RequisitionAction.Close)] = RequisitionStatus.Closed,'
        To     = "[(RequisitionStatus.Issued, RequisitionAction.Close)] = RequisitionStatus.Closed,`r`n            [(RequisitionStatus.Draft, RequisitionAction.Issue)] = RequisitionStatus.Issued,"
        Verify = 'RequisitionStatus.Draft, RequisitionAction.Issue'
    },
    @{
        Name   = 'P6  駁回不再要求填寫原因'
        Rule   = 'FR-302 駁回須填原因'
        File   = "$domain/Requisitions/Requisition.cs"
        From   = 'if (string.IsNullOrWhiteSpace(reason))'
        To     = 'if (reason is null && false)'
        Verify = 'if (reason is null && false)'
    }
)

function Invoke-TestRun {
    # 不要對原生執行檔用 2>&1：Windows PowerShell 5.1 會把 stderr 的每一行包成 ErrorRecord，
    # 在 $ErrorActionPreference='Stop' 之下變成終止錯誤 —— 而測試變紅正是探針要的結果，
    # 不是腳本該中斷的理由。
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'

    # ★ 強制 dotnet CLI 用英文輸出，讓解析與呼叫端的語系／主控台編碼無關。
    #   原本用中文的「失敗:／通過:」比對，從 Git Bash 呼叫時 dotnet 的中文輸出
    #   會以不同編碼回來，兩個 pattern 都比不到 —— 於是這道關卡在 PowerShell 裡是綠的、
    #   在 Git Bash 裡是紅的，同一份程式碼兩種結果。關卡必須與呼叫方式無關才有資格當關卡。
    $previousLang = $env:DOTNET_CLI_UI_LANGUAGE
    $env:DOTNET_CLI_UI_LANGUAGE = 'en'
    try {
        $output = (& dotnet test $TestProject --nologo) | Out-String
    }
    finally {
        $env:DOTNET_CLI_UI_LANGUAGE = $previousLang
        $ErrorActionPreference = $previous
    }

    $m = [regex]::Match($output, 'Failed:\s*(\d+).*?Passed:\s*(\d+)')
    if (-not $m.Success) {
        # 保留中文 fallback，萬一環境變數在某個 SDK 版本失效。
        $m = [regex]::Match($output, '失敗:\s*(\d+).*?通過:\s*(\d+)')
    }
    if ($m.Success) {
        return [pscustomobject]@{ Failed = [int]$m.Groups[1].Value; Passed = [int]$m.Groups[2].Value; Parsed = $true; Raw = $output }
    }

    # ★ 解析失敗與「測試真的紅了」是兩件事，必須分開回報。
    #   原本兩者都走「Failed = -1」再被基線檢查抓成「基線就不是全綠」——
    #   那個訊息會讓人去查測試，但真正的問題在輸出解析。
    return [pscustomobject]@{ Failed = -1; Passed = -1; Parsed = $false; Raw = $output }
}

Write-Host '=== 基線（未改壞）===' -ForegroundColor Cyan
$baseline = Invoke-TestRun
if (-not $baseline.Parsed) {
    Write-Host '無法從 dotnet test 的輸出解析出通過／失敗數 —— 這不是測試紅了，是解析壞了。' -ForegroundColor Red
    Write-Host '以下是實際收到的輸出尾段，請據此修正 Invoke-TestRun 的比對規則：' -ForegroundColor Yellow
    ($baseline.Raw -split "`n" | Select-Object -Last 8) | ForEach-Object { Write-Host "  $_" }
    exit 1
}
if ($baseline.Failed -ne 0) {
    Write-Host "基線就不是全綠（失敗 $($baseline.Failed)），先修好再跑探針。" -ForegroundColor Red
    exit 1
}
Write-Host "  通過 $($baseline.Passed)，失敗 0" -ForegroundColor Green
Write-Host ''

$results = @()

foreach ($probe in $probes) {
    $path = Join-Path $repoRoot $probe.File
    $originalBytes = [System.IO.File]::ReadAllBytes($path)
    $text = [System.IO.File]::ReadAllText($path)

    $verdict = ''

    if (-not $text.Contains($probe.From)) {
        $verdict = '探針目標字串不存在（實作已改動？）'
    }
    else {
        # ★ 防呆 3：改壞與還原必須包在 try/finally 裡。
        #   沒有 finally 的第一版在第一支探針就因為別的錯誤終止，
        #   還原那行永遠沒跑到 —— 改壞的產品碼就這樣留在工作區裡。
        #   若當下沒有察覺而直接 commit，送出去的就是一個「先發最晚到期」的發料系統。
        try {
            # 用字串切割而不是 regex 替換：替換字串裡的 $ 與 \ 在 regex 中有特殊意義，
            # 而我們要的是「一字不差地放進去」。
            $index = $text.IndexOf($probe.From)
            $mutated = $text.Substring(0, $index) + $probe.To + $text.Substring($index + $probe.From.Length)
            [System.IO.File]::WriteAllText($path, $mutated, (New-Object System.Text.UTF8Encoding $false))

            # ★ 防呆 1：先確認改壞真的寫進去了，再看測試結果。
            $after = [System.IO.File]::ReadAllText($path)
            if (-not $after.Contains($probe.Verify)) {
                $verdict = '改壞未生效，本次結果不採信'
            }
            else {
                $run = Invoke-TestRun
                if ($run.Failed -gt 0) {
                    $verdict = "有鑑別力：$($run.Failed) 條變紅"
                }
                else {
                    $verdict = '★ 無鑑別力：改壞了卻全綠，對應測試是空的'
                }
            }
        }
        finally {
            # ★ 防呆 2：寫回原始位元組。
            #   用「寫回」而不是「搬回備份檔」，因為寫入會把修改時間更新為現在。
            #   搬回備份檔會保留備份當下的舊時間戳，比已編譯的 dll 還舊，
            #   MSBuild 就會跳過重編，下一次測試跑的仍是帶著改壞的舊 dll。
            [System.IO.File]::WriteAllBytes($path, $originalBytes)
        }
    }

    $results += [pscustomobject]@{ Probe = $probe.Name; Rule = $probe.Rule; Verdict = $verdict }
    Write-Host ("{0}`n    {1}" -f $probe.Name, $verdict)
}

Write-Host ''
Write-Host '=== 還原後複驗（必須回到基線）===' -ForegroundColor Cyan
$final = Invoke-TestRun
if ($final.Failed -eq 0 -and $final.Passed -eq $baseline.Passed) {
    Write-Host "  通過 $($final.Passed)，失敗 0 — 與基線一致，還原乾淨。" -ForegroundColor Green
}
else {
    Write-Host "  與基線不符（通過 $($final.Passed)，失敗 $($final.Failed)）—— 還原有問題，請檢查。" -ForegroundColor Red
    exit 1
}

Write-Host ''
$results | Format-Table -AutoSize

if ($results.Verdict -match '無鑑別力|不採信|不存在') {
    Write-Host '有探針未通過，測試需要補強。' -ForegroundColor Yellow
    exit 1
}

Write-Host '全部探針皆有鑑別力。' -ForegroundColor Green
