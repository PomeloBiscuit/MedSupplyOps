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

# 整合測試共用同一個 Oracle schema。探針會暫時改動原始碼，絕不能在另一個
# testhost 正在執行時開始；拒絕比「偶爾互相污染後才出現一串無關紅燈」可診斷得多。
$testHosts = @(Get-Process -Name testhost -ErrorAction SilentlyContinue)
if ($testHosts.Count -gt 0) {
    $pids = $testHosts | ForEach-Object { $_.Id } | Sort-Object
    Write-Host ("偵測到 testhost 程序正在執行（PID: {0}）；拒絕開始鑑別力探針。" -f ($pids -join ', ')) -ForegroundColor Red
    Write-Host '請等待或停止另一個 dotnet test 程序後再執行；本腳本不會自動終止別人的測試。' -ForegroundColor Yellow
    exit 1
}

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
        # 不能只刪除：原先的 Verify 是原始碼裡本來就有的下一行註解，
        # 無法用來分辨「探針尚未打入」和「上次中斷留下突變」。
        To     = '// 探針移除：LotNumber 決勝鍵'
        Verify = '// 探針移除：LotNumber 決勝鍵'
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
    },
    @{
        Name        = 'P7  拿掉發料的 SELECT ... FOR UPDATE（並發不再序列化）'
        Rule        = 'FR-402 並發不得超發'
        File        = 'src/MedSupplyOps.Infrastructure/Services/StockIssueService.cs'
        From        = '        FOR UPDATE WAIT'
        To          = '        -- FOR UPDATE WAIT'
        Verify      = '-- FOR UPDATE WAIT'
        TestProject = 'tests/MedSupplyOps.Integration.Tests'
    },
    @{
        # 這支探針釘住的不是某條業務規則，而是「App 照使用者的方式啟動時真的能用」。
        # 它存在的理由：曾經五道關卡全綠而網站每頁 500。
        Name        = 'P8  拿掉 InventoryQueries 的 DI 註冊（正式組裝出現破洞）'
        Rule        = '啟動煙霧測試：正式 DI 圖必須完整'
        File        = 'src/MedSupplyOps.Web/Program.cs'
        From        = "builder.Services.AddScoped<InventoryQueries>(serviceProvider =>`r`n    new InventoryQueries(serviceProvider.GetRequiredService<MedSupplyOpsDbContext>().Database.GetDbConnection()));"
        To          = '// 探針移除：AddScoped<InventoryQueries>'
        Verify      = '// 探針移除：AddScoped<InventoryQueries>'
        TestProject = 'tests/MedSupplyOps.Integration.Tests'
    },
    @{
        # 把「任一筆明細失敗就整張回滾」改成「跳過繼續」，系統會變成部分發料。
        # 症狀：畫面顯示「發料失敗，庫存不足」（訊息完全正確），
        # 但前面幾個品項的庫存已經真的被扣走了，要對帳才會發現。
        Name        = 'P9  整張單發料改成「跳過失敗的明細繼續」（部分發料）'
        Rule        = 'FR-303 整張單原子發料'
        File        = 'src/MedSupplyOps.Infrastructure/Services/StockIssueService.cs'
        From        = "                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);`r`n                    return outcome.FailureReason == IssueFailureReason.LockTimeout`r`n                        ? RequisitionIssueResult.LockTimeout()`r`n                        : RequisitionIssueResult.InsufficientStock(itemId, quantity, outcome.AvailableQuantity);"
        To          = '                    continue; // 探針：改成部分發料'
        Verify      = 'continue; // 探針：改成部分發料'
        TestProject = 'tests/MedSupplyOps.Integration.Tests'
    },
    @{
        Name        = 'P10 入庫拿掉品項列的 FOR UPDATE'
        Rule        = 'D4 同品項的新批號入庫必須序列化'
        File        = 'src/MedSupplyOps.Infrastructure/Services/StockReceivingService.cs'
        From        = "          AND is_deleted = 0`r`n        FOR UPDATE WAIT"
        To          = "          AND is_deleted = 0`r`n        -- 探針移除：FOR UPDATE WAIT"
        Verify      = '-- 探針移除：FOR UPDATE WAIT'
        TestProject = 'tests/MedSupplyOps.Integration.Tests'
    },
    @{
        Name        = 'P11 入庫的過期判定 < 改成 <='
        Rule        = 'D4 效期當天仍可入庫'
        File        = 'src/MedSupplyOps.Infrastructure/Services/StockReceivingService.cs'
        From        = 'if (expiryDate < asOf)'
        To          = 'if (expiryDate <= asOf)'
        Verify      = 'if (expiryDate <= asOf)'
        TestProject = 'tests/MedSupplyOps.Integration.Tests'
    },
    @{
        # 首頁儀表板範圍迴歸：把「沒有角色」的科室範圍改回舊行為（等同「不受限」）。
        # 這個迴歸對三個角色沒有影響——RequisitionsController 的每個 Action 都要求三個角色之一，
        # 沒有角色的帳號本來就進不去，所以這支探針不會讓其他測試在共用資料庫留下殘留。
        Name        = 'P12 首頁「沒有角色」的範圍改回「不受限」（D1 迴歸）'
        Rule        = '沒有任何角色的帳號不得看到全院資料'
        File        = 'src/MedSupplyOps.Web/Authorization/DepartmentScopeResolver.cs'
        From        = 'return DepartmentScope.NoScope;'
        To          = 'return DepartmentScope.Unrestricted; // 探針 D8：沒有角色也不受限'
        Verify      = '探針 D8：沒有角色也不受限'
        TestProject = 'tests/MedSupplyOps.Integration.Tests'
    },
    @{
        # GS1：未知 AI 必須讓整筆解析失敗。若改成略過，合法三段後面夾一段 (21)
        # 仍會回傳看似完整的 GTIN／效期／批號，正是醫材條碼最危險的「部分採用」。
        Name   = 'P13 GS1 遇到未知 AI 時改成略過並繼續解析'
        Rule   = 'FR-104 GS1 不認得就整筆拒絕'
        File   = "$domain/Barcodes/Gs1BarcodeParser.cs"
        From   = "default:`r`n                    return false;"
        To     = "default:`r`n                    continue; // 探針：忽略未知 AI"
        Verify = 'continue; // 探針：忽略未知 AI'
    }
)

# 從 HEAD 直接讀取位元組，才能在自動還原時保持既有換行與編碼，不依賴工作目錄備份。
function Get-HeadFileBytes {
    param([Parameter(Mandatory = $true)][string]$RelativePath)

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo.FileName = 'git'
    $process.StartInfo.Arguments = "show --no-textconv HEAD:$RelativePath"
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    $process.StartInfo.UseShellExecute = $false
    [void]$process.Start()
    $memory = New-Object System.IO.MemoryStream
    $process.StandardOutput.BaseStream.CopyTo($memory)
    $errorOutput = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) {
        throw "無法讀取 HEAD:$RelativePath：$errorOutput"
    }

    return $memory.ToArray()
}

# 比對時正規化換行，因為 Git checkout 與本機 working tree 的 CRLF 策略可能不同；
# 除了換行外，檔案內容仍必須逐字等於「HEAD 套用這一支探針」的結果。
function ConvertTo-NormalizedText {
    param([Parameter(Mandatory = $true)][string]$Text)

    return $Text -replace "`r`n", "`n"
}

# ★ 防呆 4：探針開始前檢查任何上次中斷留下的突變。只有整個檔案精確等於
# HEAD 套用同一支探針的結果時，才可安全自動還原；混入任何其他差異一律拒絕。
# ★ 比對的是「內容」，不是位元組：BOM 與 CRLF/LF 的差異會被正規化掉
#   （ReadAllText 讀檔時會吃掉 BOM；換行由 ConvertTo-NormalizedText 統一）。
#   這是刻意的 —— 不同工具寫檔的換行與編碼不一致，嚴格比對會讓自動還原幾乎永遠不觸發。
#   代價（已實測）：檔案「只多了一個 BOM」仍會被判定為可安全還原，那個 BOM 會一併改回 HEAD；
#   內容層級的差異（多一行、改一個字）則一律拒絕 —— 這點也實測過。
$staleProbes = @(
    foreach ($probe in $probes) {
        $path = Join-Path $repoRoot $probe.File
        if (-not (Test-Path -LiteralPath $path)) {
            Write-Host ("探針目標檔不存在：{0}（{1}）" -f $probe.File, $probe.Name) -ForegroundColor Red
            exit 1
        }

        $text = [System.IO.File]::ReadAllText($path)
        if ($text.Contains($probe.Verify)) {
            $headBytes = Get-HeadFileBytes -RelativePath $probe.File
            $headText = [System.Text.Encoding]::UTF8.GetString($headBytes)
            $fromIndex = $headText.IndexOf($probe.From, [System.StringComparison]::Ordinal)
            $expectedMutation = if ($fromIndex -ge 0) {
                $headText.Substring(0, $fromIndex) + $probe.To + $headText.Substring($fromIndex + $probe.From.Length)
            }
            else {
                $null
            }
            $isExactMutation = $null -ne $expectedMutation -and
                (ConvertTo-NormalizedText -Text $text) -ceq (ConvertTo-NormalizedText -Text $expectedMutation)

            [pscustomobject]@{
                File = $probe.File
                Probe = $probe.Name
                Verify = $probe.Verify
                HeadText = $headText
                IsExactMutation = $isExactMutation
            }
        }
    }
)

if ($staleProbes.Count -gt 0) {
    $unsafeStaleProbes = @($staleProbes | Where-Object { -not $_.IsExactMutation })
    if ($unsafeStaleProbes.Count -gt 0) {
        Write-Host '偵測到上次中斷留下的探針突變，且檔案混有其他差異；拒絕開始執行。' -ForegroundColor Red
        foreach ($stale in $unsafeStaleProbes) {
            Write-Host ("  檔案：{0}`n  探針：{1}`n  Verify：{2}" -f $stale.File, $stale.Probe, $stale.Verify) -ForegroundColor Red
        }
        exit 1
    }

    foreach ($stale in $staleProbes) {
        Write-Host ("偵測到可安全自動還原的探針殘留：{0}（{1}）" -f $stale.File, $stale.Probe) -ForegroundColor Yellow
        Write-Host '還原前完整 diff：' -ForegroundColor Yellow
        $diff = & git diff --no-ext-diff -- $stale.File
        if ($LASTEXITCODE -gt 1) {
            throw "無法取得還原前 diff：$($stale.File)"
        }
        $diff | ForEach-Object { Write-Host $_ }
        $stalePath = Join-Path $repoRoot $stale.File
        $workingText = [System.IO.File]::ReadAllText($stalePath)
        $restoreText = if ($workingText.Contains("`r`n")) { $stale.HeadText -replace "`n", "`r`n" } else { $stale.HeadText }
        [System.IO.File]::WriteAllText($stalePath, $restoreText, (New-Object System.Text.UTF8Encoding $false))
        Write-Host ("已自動還原檔案：{0}`n  探針：{1}" -f $stale.File, $stale.Probe) -ForegroundColor Green
    }
}

function Invoke-TestRun {
    param([string]$Project = $TestProject)

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
        $output = (& dotnet test $Project --nologo) | Out-String
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

# 每支探針可指定自己的測試專案（並發探針要跑整合測試）。
# 基線必須針對「該探針實際會跑的那個專案」計算，否則會拿 A 專案的基線去判 B 專案的結果。
$projects = @($TestProject) + ($probes | ForEach-Object { $_.TestProject }) | Where-Object { $_ } | Select-Object -Unique
$baselines = @{}

Write-Host '=== 基線（未改壞）===' -ForegroundColor Cyan
foreach ($proj in $projects) {
    $b = Invoke-TestRun -Project $proj
    if (-not $b.Parsed) {
        Write-Host "無法從 dotnet test 的輸出解析出通過／失敗數（$proj）—— 這不是測試紅了，是解析壞了。" -ForegroundColor Red
        ($b.Raw -split "`n" | Select-Object -Last 8) | ForEach-Object { Write-Host "  $_" }
        exit 1
    }
    if ($b.Failed -ne 0) {
        Write-Host "基線就不是全綠（$proj 失敗 $($b.Failed)），先修好再跑探針。" -ForegroundColor Red
        exit 1
    }
    $baselines[$proj] = $b
    Write-Host ("  {0}: 通過 {1}，失敗 0" -f $proj, $b.Passed) -ForegroundColor Green
}
Write-Host ''

$results = @()

foreach ($probe in $probes) {
    # 沒指定就用預設專案。指定錯的話，會拿 A 專案的基線去判 B 專案的結果 ——
    # 那種比較永遠得不到「有鑑別力」，而原因完全看不出來。
    $probeProject = if ($probe.TestProject) { $probe.TestProject } else { $TestProject }
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
                $run = Invoke-TestRun -Project $probeProject
                if (-not $run.Parsed) {
                    # ★ 防呆 3：解析不到通過／失敗數 = 這次根本沒有測試跑過（多半是建置失敗）。
                    #   原本這裡會掉進下面的 else，把「零條測試執行」印成「改壞了卻全綠」——
                    #   那是最糟的誤判：它去指控測試沒有鑑別力，而真正的事實是什麼都沒跑。
                    #   2026-09-16 曾在探針執行中把一支編譯不過的暫存測試檔寫進測試專案，
                    #   P11／P12 就是這樣被誤判成無鑑別力的。
                    $tail = ($run.Raw -split "`r?`n" | Where-Object { $_ -match 'error|Build FAILED|建置失敗' } | Select-Object -First 3) -join ' / '
                    $verdict = "★ 這次沒有測到：dotnet test 沒有產出通過／失敗數，結果不採信。$tail"
                }
                elseif ($run.Failed -gt 0) {
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
$allRestored = $true
foreach ($proj in $projects) {
    $final = Invoke-TestRun -Project $proj
    if ($final.Failed -eq 0 -and $final.Passed -eq $baselines[$proj].Passed) {
        Write-Host ("  {0}: 通過 {1}，失敗 0 — 與基線一致，還原乾淨。" -f $proj, $final.Passed) -ForegroundColor Green
    }
    else {
        Write-Host ("  {0}: 與基線不符（通過 {1}，失敗 {2}；基線 {3}）—— 還原有問題。" -f $proj, $final.Passed, $final.Failed, $baselines[$proj].Passed) -ForegroundColor Red
        $allRestored = $false
    }
}
if (-not $allRestored) { exit 1 }

Write-Host ''
$results | Format-Table -AutoSize

if ($results.Verdict -match '無鑑別力|不採信|不存在') {
    Write-Host '有探針未通過，測試需要補強。' -ForegroundColor Yellow
    exit 1
}

Write-Host '全部探針皆有鑑別力。' -ForegroundColor Green
