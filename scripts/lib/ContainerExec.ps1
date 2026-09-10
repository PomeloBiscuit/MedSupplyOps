<#
.SYNOPSIS
    在 Oracle 容器內執行 bash 或 SQL*Plus 腳本。backup / restore 兩支腳本共用同一份實作。

.DESCRIPTION
    ★ 刻意**不用**「把命令用管線餵進 docker exec -i」的寫法。

    Windows PowerShell 5.1 在某些 host（例如被工具包裝過的 PowerShell）底下，
    寫入原生程式 stdin 時會在最前面加上一個 UTF-8 BOM。實測到的兩種後果：

      餵給 bash    -> bash: line 1: $'\357\273\277NLS_LANG=...': command not found
                      大聲失敗，看得見。

      餵給 sqlplus -> SP2-0734: unknown command beginning "<BOM>SET PAG..." - rest of line ignored.
                      **然後繼續執行剩下的每一行。** 第一個 SET 指令被吃掉、其餘照常，
                      而呼叫端若只檢查 ORA- 就完全看不到 —— 這是安靜失敗的那一種。

    更難查的是它**只在部分 host 發生**：同一支腳本在一般終端機跑得起來，
    透過工具跑就壞。「我這裡可以」對這種問題完全不是證據。

    所以改成：把腳本寫成**無 BOM、LF 換行**的檔案 -> docker cp 進容器 -> 執行 -> 刪掉。
    順帶避開 PowerShell 5.1 對原生命令引數的第二次引號解析
    （Data Pump 的 \"sys/...@... as sysdba\" 正需要保留那組雙引號）。
#>

Set-StrictMode -Version Latest

function Copy-AndRunInContainer {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ContainerName,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Content,
        [Parameter(Mandatory)][ValidateSet('bash', 'sqlplus')][string]$Interpreter
    )

    $extension = if ($Interpreter -eq 'bash') { 'sh' } else { 'sql' }
    $name = 'medsupplyops-{0}.{1}' -f ([guid]::NewGuid().ToString('N')), $extension
    $localPath = Join-Path ([System.IO.Path]::GetTempPath()) $name
    $containerPath = "/tmp/$name"

    # ★ 無 BOM 一定要用 UTF8Encoding($false)。
    #   [System.Text.Encoding]::UTF8 在 .NET Framework 是**帶 BOM** 的，
    #   名字看不出來，而寫出去的檔案前面就會多三個位元組。
    $utf8NoBom = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllBytes($localPath, $utf8NoBom.GetBytes(($Content -replace "`r`n", "`n")))

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $output = ''
    $exitCode = 0
    try {
        & docker cp $localPath "${ContainerName}:$containerPath" | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "docker cp 失敗：無法把腳本複製進容器 $ContainerName。"
        }

        if ($Interpreter -eq 'bash') {
            $output = (& docker exec $ContainerName bash $containerPath) | Out-String
        }
        else {
            $output = (& docker exec $ContainerName bash -lc "sqlplus -s '/ as sysdba' @$containerPath") | Out-String
        }
        $exitCode = $LASTEXITCODE
    }
    finally {
        Remove-Item -LiteralPath $localPath -Force -ErrorAction SilentlyContinue
        # docker cp 進去的檔案屬 root，容器內的預設身分（oracle）刪不掉，所以指定 -u root。
        & docker exec -u root $ContainerName rm -f $containerPath 2>$null | Out-Null
        $ErrorActionPreference = $previous
    }

    return [pscustomobject]@{ Output = $output; ExitCode = $exitCode }
}

function Invoke-ContainerSql {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ContainerName,
        [Parameter(Mandatory)][string]$Sql
    )

    $result = Copy-AndRunInContainer -ContainerName $ContainerName -Content $Sql -Interpreter sqlplus

    if ($result.ExitCode -ne 0) {
        throw "容器內 SQL*Plus 執行失敗（exit $($result.ExitCode)）：`n$($result.Output)"
    }

    # ★ ORA- 與 SP2- 都要檢查。
    #   只檢查 ORA- 會漏掉 SQL*Plus 層級的錯誤（例如某個 SET 指令沒被接受），
    #   而那類錯誤**不會讓 sqlplus 回傳非零、也不會中止後續的 SQL** ——
    #   它會安靜地少做一件事，然後把看起來完全正常的結果交給你。
    if ($result.Output -match '(ORA|SP2)-\d+') {
        throw "容器內的 SQL 回報錯誤：`n$($result.Output)"
    }

    return $result.Output
}

function Invoke-ContainerBash {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ContainerName,
        [Parameter(Mandatory)][string]$Command
    )

    $result = Copy-AndRunInContainer -ContainerName $ContainerName -Content $Command -Interpreter bash

    # ★ 這裡刻意**只看結束碼**，不像 SQL 那樣攔 ORA-。
    #   Data Pump 會把非致命的警告也寫成 ORA-xxxxx（例如中文 COMMENT metadata 的
    #   ORA-39346），把那些當成失敗會讓備份無法完成。
    #   真正的失敗由結束碼負責，警告由呼叫端自行判讀並如實記錄。
    if ($result.ExitCode -ne 0) {
        throw "容器內命令失敗（exit $($result.ExitCode)）：`n$($result.Output)"
    }

    return $result.Output
}
