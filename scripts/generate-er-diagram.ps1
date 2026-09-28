<#
.SYNOPSIS
    從圖面規格與 Oracle 資料字典產生概念模型、完整 ER 圖與關聯綱目，或檢查提交的圖是否漂移。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/generate-er-diagram.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/generate-er-diagram.ps1 -Check
#>

[CmdletBinding()]
param(
    [switch]$Check,
    [string]$ContainerName = 'medsupplyops-oracle',
    [string]$OutputPath = 'docs/diagrams/schema.mmd',
    [string]$SpecificationPath = 'docs/diagrams/conceptual-model.json',
    # ★ README 也放同一張圖。手抄一份進 README，它一定會過期，而且沒有任何關卡看得到——
    #   所以那一份也由這支腳本產生、也由 -Check 把關。見 README 的 ER-DIAGRAM 標記。
    [string]$ReadmePath = 'README.md'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

function Get-AppDatabasePassword {
    $envPath = Join-Path $repoRoot '.env'
    if (-not (Test-Path -LiteralPath $envPath)) {
        throw '.env 不存在；請依 .env.example 建立並設定 APP_DB_PASSWORD。'
    }

    foreach ($line in [System.IO.File]::ReadAllLines($envPath)) {
        if ($line -match '^\s*APP_DB_PASSWORD\s*=\s*(.*?)\s*$') {
            $password = $matches[1]
            if ([string]::IsNullOrWhiteSpace($password)) {
                break
            }
            return $password
        }
    }

    throw '.env 未設定 APP_DB_PASSWORD。'
}

function Get-DockerExecutable {
    $bundledDocker = 'D:\Docker\resources\bin\docker.exe'
    if (Test-Path -LiteralPath $bundledDocker) {
        return $bundledDocker
    }

    $command = Get-Command docker.exe -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        $command = Get-Command docker -ErrorAction SilentlyContinue
    }
    if ($null -eq $command) {
        throw '找不到 docker.exe；請先啟動並安裝 Docker Desktop。'
    }
    return $command.Source
}

function Invoke-Docker {
    param(
        [Parameter(Mandatory = $true)][string]$Docker,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    # Windows PowerShell 5.1 會把原生程式的 stderr 包成 ErrorRecord；不要讓它在
    # ErrorActionPreference=Stop 時中斷。仍以原生程式的離開碼判定成功或失敗。
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = (& $Docker @Arguments | Out-String)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }

    if ($exitCode -ne 0) {
        $displayArguments = $Arguments | ForEach-Object {
            if ($_ -match '^APP_DB_PASSWORD=') { 'APP_DB_PASSWORD=***' } else { $_ }
        }
        throw "Docker 指令失敗（exit $exitCode）：docker $($displayArguments -join ' ')"
    }
    return $output.TrimEnd("`r", "`n")
}

function Invoke-OracleDictionaryQuery {
    param(
        [Parameter(Mandatory = $true)][string]$Docker,
        [Parameter(Mandatory = $true)][string]$Password,
        [Parameter(Mandatory = $true)][string]$Container
    )

    # 每一種紀錄固定欄位數並使用 | 分隔，讓 PowerShell 不依賴 sqlplus 的顯示格式。
    # 所有資料字典查詢都有 ORDER BY，避免同一個 schema 產生不同位元組的圖。
    $sql = @'
whenever sqlerror exit failure rollback
set echo off feedback off heading off pagesize 0 linesize 32767 trimspool on tab off
-- ★ 這一段不進圖，只為了「出聲」：列出被排除的 Oracle 暫存產物。
--   回收桶（BIN$）與 DBMS_COMPRESSION 的暫存表（CMP<n>$）不是我們宣告的 schema，
--   但也不能靜靜跳過 —— 靜靜跳過的話，將來真的多出一張表也會被一起吃掉。
select 'X|' || table_name
  from user_tables
 where table_name like 'BIN$%'
    or regexp_like(table_name, '^CMP[0-9]+\$')
 order by table_name;

select 'T|' || table_name
  from user_tables
 where table_name <> 'SCHEMA_VERSIONS'
   and table_name not like 'BIN$%'
   and not regexp_like(table_name, '^CMP[0-9]+\$')
 order by table_name;

select 'C|' || c.table_name || '|' || c.column_name || '|' || c.data_type || '|' ||
       nvl(to_char(c.char_length), '') || '|' || nvl(c.char_used, '') || '|' ||
       nvl(to_char(c.data_precision), '') || '|' || nvl(to_char(c.data_scale), '')
  from user_tab_columns c
 where c.table_name <> 'SCHEMA_VERSIONS'
   and c.table_name not like 'BIN$%'
   and not regexp_like(c.table_name, '^CMP[0-9]+\$')
 order by c.table_name, c.column_id;

select 'P|' || cc.table_name || '|' || cc.column_name
  from user_constraints c
  join user_cons_columns cc on cc.constraint_name = c.constraint_name
 where c.constraint_type = 'P'
   and c.table_name <> 'SCHEMA_VERSIONS'
   and c.table_name not like 'BIN$%'
   and not regexp_like(c.table_name, '^CMP[0-9]+\$')
 order by cc.table_name, c.constraint_name, cc.position;

select 'F|' || cc.table_name || '|' || cc.column_name
  from user_constraints c
  join user_cons_columns cc on cc.constraint_name = c.constraint_name
 where c.constraint_type = 'R'
   and c.table_name <> 'SCHEMA_VERSIONS'
   and c.table_name not like 'BIN$%'
   and not regexp_like(c.table_name, '^CMP[0-9]+\$')
 order by cc.table_name, c.constraint_name, cc.position;

select 'R|' || fk.constraint_name || '|' || fk.table_name || '|' || pk.table_name || '|' ||
       case when exists (
           select 1
             from user_cons_columns fkc
             join user_tab_columns tc
               on tc.table_name = fkc.table_name
              and tc.column_name = fkc.column_name
            where fkc.constraint_name = fk.constraint_name
              and tc.nullable = 'Y'
       ) then 'Y' else 'N' end || '|' || fk.delete_rule
  from user_constraints fk
  join user_constraints pk on pk.constraint_name = fk.r_constraint_name
 where fk.constraint_type = 'R'
   and fk.table_name <> 'SCHEMA_VERSIONS'
   and fk.table_name not like 'BIN$%'
   and not regexp_like(fk.table_name, '^CMP[0-9]+\$')
   and pk.table_name <> 'SCHEMA_VERSIONS'
   and pk.table_name not like 'BIN$%'
   and not regexp_like(pk.table_name, '^CMP[0-9]+\$')
 order by fk.constraint_name;

select 'M|' || fk.constraint_name || '|' || to_char(fkc.position) || '|' ||
       fk.table_name || '|' || fkc.column_name || '|' ||
       pk.table_name || '|' || pkc.column_name || '|' || fk.delete_rule
  from user_constraints fk
  join user_cons_columns fkc on fkc.constraint_name = fk.constraint_name
  join user_constraints pk on pk.constraint_name = fk.r_constraint_name
  join user_cons_columns pkc
    on pkc.constraint_name = pk.constraint_name
   and pkc.position = fkc.position
 where fk.constraint_type = 'R'
   and fk.table_name <> 'SCHEMA_VERSIONS'
   and fk.table_name not like 'BIN$%'
   and not regexp_like(fk.table_name, '^CMP[0-9]+\$')
   and pk.table_name <> 'SCHEMA_VERSIONS'
   and pk.table_name not like 'BIN$%'
   and not regexp_like(pk.table_name, '^CMP[0-9]+\$')
 order by fk.constraint_name, fkc.position;
exit success
'@

    $encodedSql = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($sql))
    $shellCommand = "echo '$encodedSql' | base64 -d | `"`$ORACLE_HOME/bin/sqlplus`" -s `"medsupply/`$APP_DB_PASSWORD@//localhost:1521/FREEPDB1`""
    $arguments = @('exec', '-e', "APP_DB_PASSWORD=$Password", $Container, 'sh', '-lc', $shellCommand)
    $output = Invoke-Docker -Docker $Docker -Arguments $arguments

    if ([string]::IsNullOrWhiteSpace($output)) {
        throw '資料字典查詢沒有傳回資料；請確認 MEDSUPPLY schema 已建立。'
    }
    return @($output -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function ConvertTo-MermaidType {
    param([string]$DataType)

    switch -Regex ($DataType) {
        '^VARCHAR2$' { return 'varchar' }
        '^NVARCHAR2$' { return 'nvarchar' }
        '^CHAR$' { return 'char' }
        '^NCHAR$' { return 'nchar' }
        '^NUMBER$' { return 'number' }
        '^FLOAT$' { return 'number' }
        '^BINARY_FLOAT$' { return 'float' }
        '^BINARY_DOUBLE$' { return 'double' }
        '^DATE$' { return 'date' }
        '^TIMESTAMP' { return 'timestamp' }
        '^CLOB$' { return 'clob' }
        '^NCLOB$' { return 'nclob' }
        '^BLOB$' { return 'blob' }
        '^RAW$' { return 'raw' }
        default { return $DataType.ToLowerInvariant() }
    }
}

function New-MermaidDiagram {
    param([Parameter(Mandatory = $true)][string[]]$DictionaryRows)

    $tables = New-Object System.Collections.Generic.List[string]
    # 被排除的 Oracle 暫存產物（回收桶、DBMS_COMPRESSION 暫存表）。不進圖，但要印出來。
    $artifacts = New-Object System.Collections.Generic.List[string]
    $columns = @{}
    $primaryKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $foreignKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $relationships = New-Object System.Collections.Generic.List[object]

    foreach ($row in $DictionaryRows) {
        $parts = $row -split '\|', -1
        switch ($parts[0]) {
            'X' { [void]$artifacts.Add($parts[1]) }
            'T' { $tables.Add($parts[1]) }
            'C' {
                $key = $parts[1]
                if (-not $columns.ContainsKey($key)) {
                    $columns[$key] = New-Object System.Collections.Generic.List[object]
                }
                $columns[$key].Add([pscustomobject]@{
                    Name = $parts[2]
                    Type = ConvertTo-MermaidType $parts[3]
                })
            }
            'P' { [void]$primaryKeys.Add("$($parts[1])|$($parts[2])") }
            'F' { [void]$foreignKeys.Add("$($parts[1])|$($parts[2])") }
            'R' {
                $relationships.Add([pscustomobject]@{
                    Name = $parts[1]
                    ChildTable = $parts[2]
                    ParentTable = $parts[3]
                    IsOptional = $parts[4] -eq 'Y'
                })
            }
            'M' { }
            default { throw "無法辨識資料字典輸出：$row" }
        }
    }

    if ($artifacts.Count -gt 0) {
        Write-Host ('資料字典裡有 {0} 個 Oracle 暫存產物，已排除在 ER 圖之外：{1}' -f $artifacts.Count, ($artifacts -join ', ')) -ForegroundColor Yellow
        Write-Host '（BIN$ 是回收桶、CMP<n>$ 是 DBMS_COMPRESSION 的暫存表，都由資料庫自己產生；冷啟的資料庫不會有它們。）' -ForegroundColor Yellow
    }

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('<!-- 本檔由 scripts/generate-er-diagram.ps1 產生，請勿手動編輯。產生指令：powershell -NoProfile -ExecutionPolicy Bypass -File scripts/generate-er-diagram.ps1 -->')
    $lines.Add('```mermaid')
    $lines.Add('erDiagram')

    foreach ($table in $tables) {
        $lines.Add("    $table {")
        foreach ($column in $columns[$table]) {
            $attributes = New-Object System.Collections.Generic.List[string]
            $columnKey = "$table|$($column.Name)"
            if ($primaryKeys.Contains($columnKey)) { $attributes.Add('PK') }
            if ($foreignKeys.Contains($columnKey)) { $attributes.Add('FK') }
            $suffix = if ($attributes.Count -gt 0) { ' ' + ($attributes -join ', ') } else { '' }
            $lines.Add("        $($column.Type) $($column.Name)$suffix")
        }
        $lines.Add('    }')
    }

    foreach ($relationship in $relationships) {
        # 外鍵在子表且非 NULL 時，每個子列必定對應一個父列（||）；
        # 同一父列可以尚無子列或有多列（o{）。可為 NULL 的外鍵改成 o|。
        $parentCardinality = if ($relationship.IsOptional) { 'o|' } else { '||' }
        $lines.Add("    $($relationship.ParentTable) $parentCardinality--o{ $($relationship.ChildTable) : `"$($relationship.Name)`"")
    }

    $lines.Add('```')
    # .editorconfig 為 Windows 文件規定 CRLF；明確指定而非使用平台預設值，
    # 才能讓產生模式與 -Check 比較同一組位元組。
    return (($lines -join "`r`n") + "`r`n")
}

function ConvertTo-SvgText {
    param([Parameter(Mandatory = $true)][string]$Text)

    return [System.Security.SecurityElement]::Escape($Text)
}

function Get-ConceptualModelSpecification {
    $absolutePath = Join-Path $repoRoot $SpecificationPath
    if (-not (Test-Path -LiteralPath $absolutePath)) {
        throw "找不到圖面規格檔 $SpecificationPath。"
    }

    try {
        return (Get-Content -Raw -LiteralPath $absolutePath -Encoding UTF8 | ConvertFrom-Json)
    }
    catch {
        throw "無法讀取圖面規格檔 $SpecificationPath：$($_.Exception.Message)"
    }
}

function Get-DatabaseModel {
    param([Parameter(Mandatory = $true)][string[]]$DictionaryRows)

    $tables = New-Object System.Collections.Generic.List[string]
    $columns = @{}
    $primaryKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $foreignKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $mappings = New-Object System.Collections.Generic.List[object]
    foreach ($row in $DictionaryRows) {
        $parts = $row -split '\|', -1
        switch ($parts[0]) {
            'T' { [void]$tables.Add($parts[1]) }
            'C' {
                $tableName = $parts[1]
                if (-not $columns.ContainsKey($tableName)) {
                    $columns[$tableName] = New-Object System.Collections.Generic.List[string]
                }
                [void]$columns[$tableName].Add($parts[2])
            }
            'P' { [void]$primaryKeys.Add("$($parts[1])|$($parts[2])") }
            'F' { [void]$foreignKeys.Add("$($parts[1])|$($parts[2])") }
            'M' {
                [void]$mappings.Add([pscustomobject]@{
                    Constraint = $parts[1]
                    Position = [int]::Parse($parts[2], [System.Globalization.CultureInfo]::InvariantCulture)
                    ChildTable = $parts[3]
                    ChildColumn = $parts[4]
                    ParentTable = $parts[5]
                    ParentColumn = $parts[6]
                    DeleteRule = $parts[7]
                })
            }
        }
    }

    return [pscustomobject]@{
        Tables = $tables.ToArray()
        Columns = $columns
        PrimaryKeys = $primaryKeys
        ForeignKeys = $foreignKeys
        Mappings = $mappings.ToArray()
    }
}

function Assert-TableCoverage {
    param(
        [Parameter(Mandatory = $true)]$Specification,
        [Parameter(Mandatory = $true)]$DatabaseModel
    )

    $drawn = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $counts = @{}
    foreach ($property in $Specification.relationalSchemas.psobject.Properties) {
        foreach ($table in $property.Value.tables) {
            [void]$drawn.Add([string]$table)
            if (-not $counts.ContainsKey([string]$table)) { $counts[[string]$table] = 0 }
            $counts[[string]$table]++
        }
    }
    $excluded = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($table in $Specification.excludedTables) { [void]$excluded.Add([string]$table) }

    $failures = New-Object System.Collections.Generic.List[string]
    foreach ($table in $DatabaseModel.Tables) {
        if ($counts.ContainsKey($table) -and $counts[$table] -ne 1) { [void]$failures.Add("資料表 $table 在全系統關聯綱目出現 $($counts[$table]) 次，預期一次。") }
        if (-not $drawn.Contains($table) -and -not $excluded.Contains($table)) {
            [void]$failures.Add("資料表 $table 沒有歸屬於任何關聯綱目，也未列入不畫清單。")
        }
        if ($drawn.Contains($table) -and $excluded.Contains($table)) {
            [void]$failures.Add("資料表 $table 同時被畫入關聯綱目與列入不畫清單。")
        }
    }
    foreach ($table in @($drawn) + @($excluded)) {
        if ($DatabaseModel.Tables -notcontains $table) {
            [void]$failures.Add("圖面規格中的資料表 $table 不存在於 Oracle 資料字典。")
        }
    }
    if ($failures.Count -gt 0) { throw ($failures -join [Environment]::NewLine) }
}

function Get-TopologicalTableOrder {
    param(
        [Parameter(Mandatory = $true)][string[]]$Tables,
        [Parameter(Mandatory = $true)][object[]]$Mappings
    )

    $businessTables = @($Tables | Where-Object { $_ -notlike 'IDENTITY_*' } | Sort-Object)
    $identityTables = @($Tables | Where-Object { $_ -like 'IDENTITY_*' } | Sort-Object)
    $businessSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $identitySet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $businessSet.UnionWith([string[]]$businessTables)
    $identitySet.UnionWith([string[]]$identityTables)

    foreach ($mapping in $Mappings) {
        if ($businessSet.Contains($mapping.ChildTable) -and $identitySet.Contains($mapping.ParentTable)) {
            throw "無法同時滿足業務表在前與外鍵拓撲順序：$($mapping.ChildTable) 參照 $($mapping.ParentTable)。"
        }
    }

    $ordered = New-Object System.Collections.Generic.List[string]
    foreach ($group in @($businessTables, $identityTables)) {
        $remaining = New-Object System.Collections.Generic.List[string]
        foreach ($table in $group) { [void]$remaining.Add($table) }

        while ($remaining.Count -gt 0) {
            $remainingSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
            $remainingSet.UnionWith([string[]]$remaining.ToArray())
            $ready = @($remaining | Where-Object {
                $candidate = $_
                -not ($Mappings | Where-Object {
                    $_.ChildTable -eq $candidate -and $remainingSet.Contains($_.ParentTable)
                })
            } | Sort-Object)

            if ($ready.Count -eq 0) {
                throw "外鍵形成循環，無法決定關聯綱目表格順序：$($remaining -join ', ')。"
            }

            foreach ($table in $ready) {
                [void]$ordered.Add($table)
                [void]$remaining.Remove($table)
            }
        }
    }

    return $ordered.ToArray()
}

function New-LayoutBounds {
    param(
        [string]$Id, [string]$DisplayName, [string]$GroupId, [string]$Kind,
        [double]$X1, [double]$Y1, [double]$X2, [double]$Y2
    )
    return [pscustomobject]@{
        Id = $Id; DisplayName = $DisplayName; GroupId = $GroupId; Kind = $Kind
        X1 = $X1; Y1 = $Y1; X2 = $X2; Y2 = $Y2
    }
}

function Test-LayoutBoundsOverlap {
    param($Left, $Right)
    return $Left.X1 -lt $Right.X2 -and $Left.X2 -gt $Right.X1 -and
        $Left.Y1 -lt $Right.Y2 -and $Left.Y2 -gt $Right.Y1
}

function Get-LineOrientation {
    param([double]$Ax, [double]$Ay, [double]$Bx, [double]$By, [double]$Cx, [double]$Cy)
    return (($Bx - $Ax) * ($Cy - $Ay)) - (($By - $Ay) * ($Cx - $Ax))
}

function Test-LineSegmentsIntersect {
    param(
        [double]$A1x, [double]$A1y, [double]$A2x, [double]$A2y,
        [double]$B1x, [double]$B1y, [double]$B2x, [double]$B2y
    )
    if ([Math]::Max([Math]::Min($A1x, $A2x), [Math]::Min($B1x, $B2x)) -gt [Math]::Min([Math]::Max($A1x, $A2x), [Math]::Max($B1x, $B2x)) -or
        [Math]::Max([Math]::Min($A1y, $A2y), [Math]::Min($B1y, $B2y)) -gt [Math]::Min([Math]::Max($A1y, $A2y), [Math]::Max($B1y, $B2y))) { return $false }
    $o1 = Get-LineOrientation $A1x $A1y $A2x $A2y $B1x $B1y
    $o2 = Get-LineOrientation $A1x $A1y $A2x $A2y $B2x $B2y
    $o3 = Get-LineOrientation $B1x $B1y $B2x $B2y $A1x $A1y
    $o4 = Get-LineOrientation $B1x $B1y $B2x $B2y $A2x $A2y
    return (($o1 -le 0 -and $o2 -ge 0) -or ($o1 -ge 0 -and $o2 -le 0)) -and
        (($o3 -le 0 -and $o4 -ge 0) -or ($o3 -ge 0 -and $o4 -le 0))
}

function Test-LineIntersectsBounds {
    param($Line, $Bounds)
    if ([Math]::Max($Line.X1, $Line.X2) -lt $Bounds.X1 -or [Math]::Min($Line.X1, $Line.X2) -gt $Bounds.X2 -or
        [Math]::Max($Line.Y1, $Line.Y2) -lt $Bounds.Y1 -or [Math]::Min($Line.Y1, $Line.Y2) -gt $Bounds.Y2) {
        return $false
    }
    if (($Line.X1 -ge $Bounds.X1 -and $Line.X1 -le $Bounds.X2 -and $Line.Y1 -ge $Bounds.Y1 -and $Line.Y1 -le $Bounds.Y2) -or
        ($Line.X2 -ge $Bounds.X1 -and $Line.X2 -le $Bounds.X2 -and $Line.Y2 -ge $Bounds.Y1 -and $Line.Y2 -le $Bounds.Y2)) {
        return $true
    }
    return (Test-LineSegmentsIntersect $Line.X1 $Line.Y1 $Line.X2 $Line.Y2 $Bounds.X1 $Bounds.Y1 $Bounds.X2 $Bounds.Y1) -or
        (Test-LineSegmentsIntersect $Line.X1 $Line.Y1 $Line.X2 $Line.Y2 $Bounds.X2 $Bounds.Y1 $Bounds.X2 $Bounds.Y2) -or
        (Test-LineSegmentsIntersect $Line.X1 $Line.Y1 $Line.X2 $Line.Y2 $Bounds.X2 $Bounds.Y2 $Bounds.X1 $Bounds.Y2) -or
        (Test-LineSegmentsIntersect $Line.X1 $Line.Y1 $Line.X2 $Line.Y2 $Bounds.X1 $Bounds.Y2 $Bounds.X1 $Bounds.Y1)
}

function Get-ShapeBoundaryPoint {
    param($Shape, [double]$TargetX, [double]$TargetY)
    $dx = $TargetX - $Shape.X
    $dy = $TargetY - $Shape.Y
    if ([Math]::Abs($dx) -lt 0.001 -and [Math]::Abs($dy) -lt 0.001) {
        throw "圖形 $($Shape.DisplayName) 與連線另一端使用相同中心點。"
    }
    if ($Shape.Kind -eq 'ellipse') {
        $scale = 1 / [Math]::Sqrt(($dx * $dx / ($Shape.Rx * $Shape.Rx)) + ($dy * $dy / ($Shape.Ry * $Shape.Ry)))
    }
    elseif ($Shape.Kind -eq 'diamond') {
        $scale = 1 / (([Math]::Abs($dx) / ($Shape.Width / 2)) + ([Math]::Abs($dy) / ($Shape.Height / 2)))
    }
    else {
        $xScale = if ([Math]::Abs($dx) -lt 0.001) { [double]::PositiveInfinity } else { ($Shape.Width / 2) / [Math]::Abs($dx) }
        $yScale = if ([Math]::Abs($dy) -lt 0.001) { [double]::PositiveInfinity } else { ($Shape.Height / 2) / [Math]::Abs($dy) }
        $scale = [Math]::Min($xScale, $yScale)
    }
    return [pscustomobject]@{ X = $Shape.X + ($dx * $scale); Y = $Shape.Y + ($dy * $scale) }
}

function Get-EstimatedTextWidth {
    param([string]$Value, [double]$FontSize)
    $units = 0.0
    foreach ($character in $Value.ToCharArray()) {
        $code = [int][char]$character
        if ($character -eq ' ') { $units += 0.3 }
        elseif (($code -ge 0x2e80 -and $code -le 0x9fff) -or ($code -ge 0xac00 -and $code -le 0xd7af) -or ($code -ge 0xf900 -and $code -le 0xfaff) -or ($code -ge 0xff00 -and $code -le 0xffef)) { $units += 1.0 }
        else { $units += 0.62 }
    }
    return ($units * $FontSize)
}

function Get-BilingualText {
    param($Element, [string]$Language)
    if ($Language -eq 'en') { return [string]$Element.nameEn }
    return [string]$Element.name
}

function Assert-ConceptualNames {
    param($Diagram)
    foreach ($element in @($Diagram.entities) + @($Diagram.relationships)) {
        foreach ($item in @($element) + @($element.attributes)) {
            if ([string]::IsNullOrWhiteSpace($item.name) -or [string]::IsNullOrWhiteSpace($item.nameEn)) {
                throw "元素 $($item.id) 缺少 name 或 nameEn。"
            }
        }
    }
}


function ConvertTo-LayoutNumber {
    param($Value, [string]$Id, [string]$Field)
    Set-StrictMode -Version Latest
    $type = if ($null -eq $Value) { 'null' } else { $Value.GetType().FullName }
    if (@($Value).Count -ne 1 -or $Value -isnot [System.ValueType]) {
        throw "元素 $Id 的 $Field 必須是單一數值，實際型別 $type。"
    }
    try { $number = [Convert]::ToDouble($Value, [Globalization.CultureInfo]::InvariantCulture) }
    catch { throw "元素 $Id 的 $Field 無法轉為 double，原始型別 $type。" }
    if ([double]::IsNaN($number) -or [double]::IsInfinity($number)) {
        throw "元素 $Id 的 $Field 不是有限數值，原始型別 $type。"
    }
    return $number
}

function New-LayoutPoint {
    param($X, $Y, [string]$Id)
    Set-StrictMode -Version Latest
    return [pscustomobject]@{ X = (ConvertTo-LayoutNumber $X $Id 'X'); Y = (ConvertTo-LayoutNumber $Y $Id 'Y') }
}

function Assert-LayoutNumbers {
    param($Layout)
    Set-StrictMode -Version Latest
    foreach ($shape in $Layout.Shapes) {
        foreach ($field in @('X', 'Y', 'Width', 'Height')) {
            $v = $shape.$field
            $type = if ($null -eq $v) { 'null' } else { $v.GetType().FullName }
            if (@($v).Count -ne 1 -or ($v -isnot [double] -and $v -isnot [int])) {
                throw "元素 $($shape.Id) 的 $field 必須是單一數值，實際型別 $type。"
            }
        }
    }
    foreach ($line in @($Layout.AttributeConnections) + @($Layout.Connections)) {
        foreach ($field in @('X1', 'Y1', 'X2', 'Y2')) {
            $v = $line.$field
            $type = if ($null -eq $v) { 'null' } else { $v.GetType().FullName }
            if (@($v).Count -ne 1 -or ($v -isnot [double] -and $v -isnot [int])) {
                throw "線 $($line.Id) 的 $field 必須是單一數值，實際型別 $type。"
            }
        }
    }
}

function Get-SystemShapeBounds {
    param($Shape)
    Set-StrictMode -Version Latest
    return (New-LayoutBounds $Shape.Id $Shape.Id $Shape.Id $Shape.Kind `
        ($Shape.X - $Shape.Width / 2) ($Shape.Y - $Shape.Height / 2) `
        ($Shape.X + $Shape.Width / 2) ($Shape.Y + $Shape.Height / 2))
}

function Get-SystemSegments {
    param($Layout)
    Set-StrictMode -Version Latest
    $segments = [System.Collections.Generic.List[object]]::new()
    foreach ($line in $Layout.AttributeConnections) { [void]$segments.Add($line) }
    foreach ($line in $Layout.Connections) {
        $dx = $line.X2 - $line.X1; $dy = $line.Y2 - $line.Y1
        $length = [Math]::Sqrt($dx * $dx + $dy * $dy)
        $offsets = if ($line.Participant.participation -eq 'total') { @(-3, 3) } else { @(0) }
        foreach ($offset in $offsets) {
            [void]$segments.Add([pscustomobject]@{
                Id = $line.Id; Owner = $line.Entity.Id; Target = $line.Diamond.Id
                X1 = $line.X1 - $dy * $offset / $length
                Y1 = $line.Y1 + $dx * $offset / $length
                X2 = $line.X2 - $dy * $offset / $length
                Y2 = $line.Y2 + $dx * $offset / $length
            })
        }
    }
    return [pscustomobject]@{ Items = $segments.ToArray() }
}

function Get-SystemGroupBounds {
    param($Layout, [string]$EntityId)
    Set-StrictMode -Version Latest
    $x1 = [double]::PositiveInfinity; $y1 = [double]::PositiveInfinity
    $x2 = [double]::NegativeInfinity; $y2 = [double]::NegativeInfinity
    foreach ($shape in $Layout.Shapes | Where-Object { $_.Id -eq "entity:$EntityId" -or $_.Id.StartsWith("attribute:${EntityId}:") }) {
        $box = Get-SystemShapeBounds $shape
        $x1 = [Math]::Min($x1, $box.X1); $y1 = [Math]::Min($y1, $box.Y1)
        $x2 = [Math]::Max($x2, $box.X2); $y2 = [Math]::Max($y2, $box.Y2)
    }
    foreach ($line in $Layout.AttributeConnections | Where-Object { $_.Owner -eq "entity:$EntityId" }) {
        $x1 = [Math]::Min($x1, [Math]::Min($line.X1, $line.X2))
        $y1 = [Math]::Min($y1, [Math]::Min($line.Y1, $line.Y2))
        $x2 = [Math]::Max($x2, [Math]::Max($line.X1, $line.X2))
        $y2 = [Math]::Max($y2, [Math]::Max($line.Y1, $line.Y2))
    }
    return (New-LayoutBounds "group:$EntityId" "group:$EntityId" $EntityId 'group' $x1 $y1 $x2 $y2)
}

function Get-SystemGroupConflict {
    param($Layout, $Diagram)
    Set-StrictMode -Version Latest
    $groups = @($Diagram.entities | ForEach-Object { Get-SystemGroupBounds $Layout ([string]$_.id) })
    for ($i = 0; $i -lt $groups.Count; $i++) {
        for ($j = $i + 1; $j -lt $groups.Count; $j++) {
            $a = $groups[$i]; $b = $groups[$j]
            $dx = [Math]::Max(0, [Math]::Max($a.X1 - $b.X2, $b.X1 - $a.X2))
            $dy = [Math]::Max(0, [Math]::Max($a.Y1 - $b.Y2, $b.Y1 - $a.Y2))
            $distance = [Math]::Sqrt($dx * $dx + $dy * $dy)
            if ($distance -lt 23.999) { return [pscustomobject]@{ A = $a; B = $b; Distance = $distance } }
        }
    }
    return $null
}

function Get-PointSegmentDistance {
    param([double]$Px, [double]$Py, $Line)
    Set-StrictMode -Version Latest
    $dx = $Line.X2 - $Line.X1; $dy = $Line.Y2 - $Line.Y1
    $lengthSquared = $dx * $dx + $dy * $dy
    $t = if ($lengthSquared -lt 0.000001) { 0.0 } else {
        [Math]::Max(0, [Math]::Min(1, (($Px - $Line.X1) * $dx + ($Py - $Line.Y1) * $dy) / $lengthSquared))
    }
    $x = $Line.X1 + $t * $dx; $y = $Line.Y1 + $t * $dy
    return [Math]::Sqrt(($Px - $x) * ($Px - $x) + ($Py - $y) * ($Py - $y))
}

function Get-SegmentBoundsDistance {
    param($Line, $Box)
    Set-StrictMode -Version Latest
    if (Test-LineIntersectsBounds $Line $Box) { return 0.0 }
    $distance = [double]::PositiveInfinity
    foreach ($point in @(
        (New-LayoutPoint $Box.X1 $Box.Y1 $Box.Id), (New-LayoutPoint $Box.X2 $Box.Y1 $Box.Id),
        (New-LayoutPoint $Box.X2 $Box.Y2 $Box.Id), (New-LayoutPoint $Box.X1 $Box.Y2 $Box.Id)
    )) {
        $distance = [Math]::Min($distance, (Get-PointSegmentDistance $point.X $point.Y $Line))
    }
    foreach ($point in @((New-LayoutPoint $Line.X1 $Line.Y1 $Line.Id), (New-LayoutPoint $Line.X2 $Line.Y2 $Line.Id))) {
        $nearX = [Math]::Max($Box.X1, [Math]::Min($Box.X2, $point.X))
        $nearY = [Math]::Max($Box.Y1, [Math]::Min($Box.Y2, $point.Y))
        $distance = [Math]::Min($distance, [Math]::Sqrt(($point.X - $nearX) * ($point.X - $nearX) + ($point.Y - $nearY) * ($point.Y - $nearY)))
    }
    return $distance
}

function Set-SystemCardinalityLabels {
    param($Layout)
    Set-StrictMode -Version Latest
    $segments = @((Get-SystemSegments $Layout).Items)
    $shapeBoxes = @($Layout.Shapes | ForEach-Object { Get-SystemShapeBounds $_ })
    $labels = [System.Collections.Generic.List[object]]::new()
    foreach ($connection in $Layout.Connections) {
        $dx = $connection.X2 - $connection.X1; $dy = $connection.Y2 - $connection.Y1
        $length = [Math]::Sqrt($dx * $dx + $dy * $dy)
        $ux = $dx / $length; $uy = $dy / $length
        $normal = if ([Math]::Abs($dx) -lt 0.001) {
            New-LayoutPoint (1.0) (0.0) $connection.Id
        } elseif ([Math]::Abs($dy) -lt 0.001) {
            New-LayoutPoint (0.0) (-1.0) $connection.Id
        } else { New-LayoutPoint (-$uy) $ux $connection.Id }
        $cardinality = [string]$connection.Participant.cardinality
        $boxWidth = [Math]::Ceiling((Get-EstimatedTextWidth $cardinality 13) + 4)
        $boxHeight = 19.0
        $lineHalfWidth = if ($connection.Participant.participation -eq 'total') { 3.75 } else { 0.75 }
        $projectedHalf = [Math]::Abs($normal.X) * $boxWidth / 2 + [Math]::Abs($normal.Y) * $boxHeight / 2
        $labelOffset = $lineHalfWidth + $projectedHalf + 4
        $selected = $null; $blockers = [System.Collections.Generic.List[string]]::new()
        foreach ($along in @(14, 26, 38)) {
            foreach ($side in @(1, -1)) {
                $cx = $connection.X1 + $ux * $along + $normal.X * $labelOffset * $side
                $cy = $connection.Y1 + $uy * $along + $normal.Y * $labelOffset * $side
                $box = New-LayoutBounds "label:$($connection.Id)" "label:$($connection.Id)" $connection.Id 'label' `
                    ($cx - $boxWidth / 2) ($cy - $boxHeight / 2) ($cx + $boxWidth / 2) ($cy + $boxHeight / 2)
                $blocker = $null
                foreach ($shapeBox in $shapeBoxes) {
                    if (Test-LayoutBoundsOverlap $box $shapeBox) { $blocker = $shapeBox.Id; break }
                }
                if ($null -eq $blocker) {
                    foreach ($segment in $segments) {
                        if ((Get-SegmentBoundsDistance $segment $box) -lt 0.75) { $blocker = "線 $($segment.Id)"; break }
                    }
                }
                if ($null -eq $blocker) {
                    foreach ($label in $labels) {
                        if (Test-LayoutBoundsOverlap $box $label.Box) { $blocker = $label.Id; break }
                    }
                }
                if ($null -eq $blocker) {
                    $selected = [pscustomobject]@{ Id = $box.Id; Connection = $connection; X = $cx; Y = $cy; Width = $boxWidth; Height = $boxHeight; Box = $box }
                    break
                }
                [void]$blockers.Add($blocker)
            }
            if ($null -ne $selected) { break }
        }
        if ($null -eq $selected) {
            throw "基數標籤 label:$($connection.Id) 候選位置全部衝突：$($blockers.ToArray() -join ', ')。"
        }
        [void]$labels.Add($selected)
    }
    $Layout | Add-Member -NotePropertyName Labels -NotePropertyValue $labels -Force
}

function Test-ColumnArrangement {
    param($EntityShape, [string]$Side, [object[]]$Attributes, [object[]]$Sizes, [int[]]$Order, [double]$Gap)
    Set-StrictMode -Version Latest
    $count = $Order.Count
    $maxWidth = ($Sizes | Measure-Object Width -Maximum).Maximum
    $direction = if ($Side -eq 'left') { -1 } else { 1 }
    $x = $EntityShape.X + $direction * ($EntityShape.Width / 2 + $Gap + $maxWidth / 2)
    $trialShapes = [System.Collections.Generic.List[object]]::new()
    for ($slot = 0; $slot -lt $count; $slot++) {
        $index = $Order[$slot]
        [void]$trialShapes.Add([pscustomobject]@{
            Id = "attribute:$($EntityShape.Element.id):$($Attributes[$index].id)"
            Kind = 'ellipse'; X = $x
            Y = $EntityShape.Y + ($slot - ($count - 1) / 2) * ($Sizes[$index].Height + 12)
            Width = $Sizes[$index].Width; Height = $Sizes[$index].Height
            Rx = $Sizes[$index].Width / 2; Ry = $Sizes[$index].Height / 2
        })
    }
    foreach ($attr in $trialShapes) {
        $start = Get-ShapeBoundaryPoint $EntityShape $attr.X $attr.Y
        $end = Get-ShapeBoundaryPoint $attr $EntityShape.X $EntityShape.Y
        $line = [pscustomobject]@{ X1 = $start.X; Y1 = $start.Y; X2 = $end.X; Y2 = $end.Y }
        foreach ($other in $trialShapes) {
            if ($other.Id -eq $attr.Id) { continue }
            if (Test-LineIntersectsBounds $line (Get-SystemShapeBounds $other)) { return $false }
        }
    }
    return $true
}

function Get-ColumnArrangement {
    param($EntityShape, [string]$Side, [object[]]$Attributes, [object[]]$Sizes)
    Set-StrictMode -Version Latest
    $count = $Attributes.Count
    $permutations = [System.Collections.Generic.List[object]]::new()
    [void]$permutations.Add([pscustomobject]@{ Indices = [int[]]@() })
    for ($index = 0; $index -lt $count; $index++) {
        $next = [System.Collections.Generic.List[object]]::new()
        foreach ($permutation in $permutations) {
            for ($position = 0; $position -le $permutation.Indices.Count; $position++) {
                $indices = [System.Collections.Generic.List[int]]::new()
                for ($i = 0; $i -lt $position; $i++) { [void]$indices.Add($permutation.Indices[$i]) }
                [void]$indices.Add($index)
                for ($i = $position; $i -lt $permutation.Indices.Count; $i++) { [void]$indices.Add($permutation.Indices[$i]) }
                [void]$next.Add([pscustomobject]@{ Indices = $indices.ToArray() })
            }
        }
        $permutations = $next
    }
    foreach ($gap in @(32.0, 36.0, 40.0)) {
        foreach ($permutation in $permutations) {
            if (Test-ColumnArrangement $EntityShape $Side $Attributes $Sizes $permutation.Indices $gap) {
                return [pscustomobject]@{ Indices = $permutation.Indices; Gap = $gap }
            }
        }
    }
    throw "實體 $($EntityShape.Element.id) 所有屬性排序在 28～40px 距離內仍與其他橢圓相交。"
}

function Get-DirectConceptualLayout {
    param($Diagram)
    Set-StrictMode -Version Latest
    $requiredSides = @{
        DEPARTMENT = 'top'; REQUISITION = 'right'; REQUISITION_LINE = 'right'
        ITEM = 'left'; USER = 'top'; ROLE = 'top'; AUDIT_LOG = 'left'
        STOCK_LOT = 'bottom'; STORAGE_LOCATION = 'bottom'
    }
    foreach ($entity in $Diagram.entities) {
        if (-not $requiredSides.ContainsKey([string]$entity.id) -or [string]$entity.attributeSide -ne $requiredSides[[string]$entity.id]) {
            throw "實體 $($entity.id) 屬性邊檢查失敗：預期 $($requiredSides[[string]$entity.id])，實際 $($entity.attributeSide)。"
        }
    }
    $columnGap = 660; $rowGap = 430
    $minimumColumnGap = 500; $lastConflict = $null
    while ($true) {
        $shapes = [System.Collections.Generic.List[object]]::new()
        $attributeConnections = [System.Collections.Generic.List[object]]::new()
        $connections = [System.Collections.Generic.List[object]]::new()
        $entityShapes = @{}; $relationshipShapes = @{}
        foreach ($entity in $Diagram.entities) {
            $row = [int]$entity.grid.row; $col = [int]$entity.grid.col
            if ($row -lt 0 -or $row -gt 3 -or $col -lt 0 -or $col -gt 2) { throw "實體 $($entity.id) 的 grid 超出 4×3。" }
            $id = "entity:$($entity.id)"
            $labelWidth = [Math]::Max((Get-EstimatedTextWidth $entity.name 15), (Get-EstimatedTextWidth $entity.nameEn 15))
            $shape = [pscustomobject]@{
                Id = $id; Kind = 'rect'; Element = $entity
                X = (ConvertTo-LayoutNumber (300 + $columnGap * $col) $id 'X')
                Y = (ConvertTo-LayoutNumber (200 + $rowGap * $row) $id 'Y')
                Width = (ConvertTo-LayoutNumber ([Math]::Ceiling($labelWidth + 28)) $id 'Width')
                Height = (ConvertTo-LayoutNumber 52 $id 'Height')
                FontSize = 15; PrimaryKey = $false
            }
            $entityShapes[[string]$entity.id] = $shape; [void]$shapes.Add($shape)
            $attributes = @($entity.attributes)
            $sizes = [System.Collections.Generic.List[object]]::new()
            foreach ($attribute in $attributes) {
                $attrId = "attribute:$($entity.id):$($attribute.id)"
                $textWidth = [Math]::Max((Get-EstimatedTextWidth $attribute.name 13), (Get-EstimatedTextWidth $attribute.nameEn 13))
                [void]$sizes.Add([pscustomobject]@{ Width = (ConvertTo-LayoutNumber ([Math]::Ceiling($textWidth + 24)) $attrId 'Width'); Height = 36.0 })
            }
            $side = [string]$entity.attributeSide
            $horizontal = $side -eq 'top' -or $side -eq 'bottom'
            $columnGapFromEntity = 32.0
            if (-not $horizontal) {
                $arrangement = Get-ColumnArrangement $shape $side $attributes $sizes
                $arrangedAttrs = New-Object 'object[]' $attributes.Count
                $arrangedSizes = New-Object 'object[]' $attributes.Count
                for ($order = 0; $order -lt $attributes.Count; $order++) {
                    $arrangedAttrs[$order] = $attributes[$arrangement.Indices[$order]]
                    $arrangedSizes[$order] = $sizes[$arrangement.Indices[$order]]
                }
                $attributes = $arrangedAttrs
                $sizes = $arrangedSizes
                $columnGapFromEntity = $arrangement.Gap
            }
            $sum = 0.0; $maxWidth = 0.0
            foreach ($size in $sizes) {
                if ($horizontal) { $sum += $size.Width } else { $sum += $size.Height }
                $maxWidth = [Math]::Max($maxWidth, $size.Width)
            }
            $sum += [Math]::Max(0, $sizes.Count - 1) * 12
            $cursor = -$sum / 2
            for ($index = 0; $index -lt $attributes.Count; $index++) {
                $attribute = $attributes[$index]; $size = $sizes[$index]
                $attrId = "attribute:$($entity.id):$($attribute.id)"
                if ($horizontal) {
                    $direction = if ($side -eq 'top') { -1 } else { 1 }
                    $point = New-LayoutPoint ($shape.X + $cursor + $size.Width / 2) `
                        ($shape.Y + $direction * ($shape.Height / 2 + 32 + $size.Height / 2)) $attrId
                    $cursor += $size.Width + 12
                } else {
                    $direction = if ($side -eq 'left') { -1 } else { 1 }
                    $point = New-LayoutPoint ($shape.X + $direction * ($shape.Width / 2 + $columnGapFromEntity + $maxWidth / 2)) `
                        ($shape.Y + $cursor + $size.Height / 2) $attrId
                    $cursor += $size.Height + 12
                }
                $attr = [pscustomobject]@{
                    Id = $attrId; Kind = 'ellipse'; Element = $attribute
                    X = (ConvertTo-LayoutNumber $point.X $attrId 'X')
                    Y = (ConvertTo-LayoutNumber $point.Y $attrId 'Y')
                    Width = $size.Width; Height = $size.Height
                    Rx = $size.Width / 2; Ry = $size.Height / 2
                    FontSize = 13; PrimaryKey = ($null -ne $attribute.PSObject.Properties['primaryKey'] -and [bool]$attribute.primaryKey)
                }
                Assert-LayoutNumbers ([pscustomobject]@{ Shapes = @($attr); AttributeConnections = @(); Connections = @() })
                [void]$shapes.Add($attr)
                $start = Get-ShapeBoundaryPoint $shape $attr.X $attr.Y
                $end = Get-ShapeBoundaryPoint $attr $shape.X $shape.Y
                [void]$attributeConnections.Add([pscustomobject]@{
                    Id = "$($shape.Id):$($attr.Id)"; Owner = $shape.Id; Attribute = $attr.Id
                    X1 = (ConvertTo-LayoutNumber $start.X $attrId 'X1')
                    Y1 = (ConvertTo-LayoutNumber $start.Y $attrId 'Y1')
                    X2 = (ConvertTo-LayoutNumber $end.X $attrId 'X2')
                    Y2 = (ConvertTo-LayoutNumber $end.Y $attrId 'Y2')
                })
            }
        }
        foreach ($relationship in $Diagram.relationships) {
            $leftSpec = @($Diagram.entities | Where-Object { $_.id -eq $relationship.participants[0].entity })[0]
            $rightSpec = @($Diagram.entities | Where-Object { $_.id -eq $relationship.participants[1].entity })[0]
            if ($null -eq $leftSpec -or $null -eq $rightSpec) { throw "關聯 $($relationship.id) 指向不存在的實體。" }
            $rowDistance = [Math]::Abs([int]$leftSpec.grid.row - [int]$rightSpec.grid.row)
            $colDistance = [Math]::Abs([int]$leftSpec.grid.col - [int]$rightSpec.grid.col)
            if ($rowDistance -gt 1 -or $colDistance -gt 1) { throw "關聯 $($relationship.id) 線段長度超過兩格中心距離：row=$rowDistance col=$colDistance。" }
            $left = $entityShapes[[string]$leftSpec.id]; $right = $entityShapes[[string]$rightSpec.id]
            $id = "relationship:$($relationship.id)"
            $labelWidth = [Math]::Max((Get-EstimatedTextWidth $relationship.name 15), (Get-EstimatedTextWidth $relationship.nameEn 15))
            $diamondWidth = [Math]::Ceiling(($labelWidth + 24) / 0.72)
            $diamond = [pscustomobject]@{
                Id = $id; Kind = 'diamond'; Element = $relationship
                X = (ConvertTo-LayoutNumber (($left.X + $right.X) / 2) $id 'X')
                Y = (ConvertTo-LayoutNumber (($left.Y + $right.Y) / 2) $id 'Y')
                Width = (ConvertTo-LayoutNumber $diamondWidth $id 'Width')
                Height = (ConvertTo-LayoutNumber 62 $id 'Height')
                FontSize = 15; PrimaryKey = $false
            }
            $relationshipShapes[[string]$relationship.id] = $diamond; [void]$shapes.Add($diamond)
            foreach ($participant in $relationship.participants) {
                $entity = $entityShapes[[string]$participant.entity]
                $start = Get-ShapeBoundaryPoint $entity $diamond.X $diamond.Y
                $end = Get-ShapeBoundaryPoint $diamond $entity.X $entity.Y
                $lineId = "$($relationship.id):$($participant.entity)"
                [void]$connections.Add([pscustomobject]@{
                    Id = $lineId; Relationship = $relationship; Participant = $participant
                    Entity = $entity; Diamond = $diamond
                    X1 = (ConvertTo-LayoutNumber $start.X $lineId 'X1')
                    Y1 = (ConvertTo-LayoutNumber $start.Y $lineId 'Y1')
                    X2 = (ConvertTo-LayoutNumber $end.X $lineId 'X2')
                    Y2 = (ConvertTo-LayoutNumber $end.Y $lineId 'Y2')
                    Straight = $true
                })
            }
            foreach ($attribute in @($relationship.attributes)) {
                $attrId = "attribute:$($relationship.id):$($attribute.id)"
                $textWidth = [Math]::Max((Get-EstimatedTextWidth $attribute.name 13), (Get-EstimatedTextWidth $attribute.nameEn 13))
                $diameter = [Math]::Ceiling($textWidth + 24)
                $vx = $right.X - $left.X; $vy = $right.Y - $left.Y
                $length = [Math]::Sqrt($vx * $vx + $vy * $vy)
                $normal = New-LayoutPoint ($vy / $length) (-$vx / $length) $attrId
                $distance = [Math]::Max($diamond.Width, $diamond.Height) / 2 + 32 + $diameter / 2
                $point = New-LayoutPoint ($diamond.X + $normal.X * $distance) ($diamond.Y + $normal.Y * $distance) $attrId
                $attr = [pscustomobject]@{
                    Id = $attrId; Kind = 'ellipse'; Element = $attribute
                    X = (ConvertTo-LayoutNumber $point.X $attrId 'X')
                    Y = (ConvertTo-LayoutNumber $point.Y $attrId 'Y')
                    Width = (ConvertTo-LayoutNumber $diameter $attrId 'Width')
                    Height = (ConvertTo-LayoutNumber 42 $attrId 'Height')
                    Rx = $diameter / 2; Ry = 21.0; FontSize = 13; PrimaryKey = $false
                }
                Assert-LayoutNumbers ([pscustomobject]@{ Shapes = @($attr); AttributeConnections = @(); Connections = @() })
                [void]$shapes.Add($attr)
                $start = Get-ShapeBoundaryPoint $diamond $attr.X $attr.Y
                $end = Get-ShapeBoundaryPoint $attr $diamond.X $diamond.Y
                [void]$attributeConnections.Add([pscustomobject]@{
                    Id = "$($diamond.Id):$($attr.Id)"; Owner = $diamond.Id; Attribute = $attr.Id
                    X1 = (ConvertTo-LayoutNumber $start.X $attrId 'X1')
                    Y1 = (ConvertTo-LayoutNumber $start.Y $attrId 'Y1')
                    X2 = (ConvertTo-LayoutNumber $end.X $attrId 'X2')
                    Y2 = (ConvertTo-LayoutNumber $end.Y $attrId 'Y2')
                })
            }
        }
        $layout = [pscustomobject]@{
            Width = 0; Height = 0; Shapes = $shapes; AttributeConnections = $attributeConnections
            Connections = $connections; EntityShapes = $entityShapes; RelationshipShapes = $relationshipShapes
            Diagram = $Diagram
        }
        Assert-LayoutNumbers $layout
        $conflict = Get-SystemGroupConflict $layout $Diagram
        if ($null -ne $conflict) {
            $aSpec = @($Diagram.entities | Where-Object { $_.id -eq $conflict.A.GroupId })[0]
            $bSpec = @($Diagram.entities | Where-Object { $_.id -eq $conflict.B.GroupId })[0]
            if ($aSpec.grid.col -ne $bSpec.grid.col) {
                $columnGap += 20
                $minimumColumnGap = [Math]::Max($minimumColumnGap, $columnGap)
            }
            if ($aSpec.grid.row -ne $bSpec.grid.row) { $rowGap += 20 }
            $lastConflict = $conflict
            $shapeBoxes = @($shapes | ForEach-Object { Get-SystemShapeBounds $_ })
            $shapeWidth = [Math]::Ceiling((($shapeBoxes | Measure-Object X2 -Maximum).Maximum) - (($shapeBoxes | Measure-Object X1 -Minimum).Minimum) + 48)
            if ($shapeWidth -gt 1800) {
                throw "實體整組外框間距不足：$($conflict.A.Id) [$($conflict.A.X1),$($conflict.A.Y1),$($conflict.A.X2),$($conflict.A.Y2)] 與 $($conflict.B.Id) [$($conflict.B.X1),$($conflict.B.Y1),$($conflict.B.X2),$($conflict.B.Y2)]，距離 $([Math]::Round($conflict.Distance,2))px；畫布寬度上限 1800px。"
            }
            continue
        }
        Set-SystemCardinalityLabels $layout
        $content = [System.Collections.Generic.List[object]]::new()
        foreach ($shape in $shapes) { [void]$content.Add((Get-SystemShapeBounds $shape)) }
        foreach ($label in $layout.Labels) { [void]$content.Add($label.Box) }
        foreach ($line in @((Get-SystemSegments $layout).Items)) {
            [void]$content.Add((New-LayoutBounds $line.Id $line.Id $line.Id 'line' `
                ([Math]::Min($line.X1,$line.X2) - 0.75) ([Math]::Min($line.Y1,$line.Y2) - 0.75) `
                ([Math]::Max($line.X1,$line.X2) + 0.75) ([Math]::Max($line.Y1,$line.Y2) + 0.75)))
        }
        $minX = [double]::PositiveInfinity; $minY = [double]::PositiveInfinity
        $maxX = [double]::NegativeInfinity; $maxY = [double]::NegativeInfinity
        foreach ($box in $content) {
            $minX = [Math]::Min($minX,$box.X1); $minY = [Math]::Min($minY,$box.Y1)
            $maxX = [Math]::Max($maxX,$box.X2); $maxY = [Math]::Max($maxY,$box.Y2)
        }
        $width = [Math]::Ceiling($maxX - $minX + 48)
        if ($width -gt 1800) {
            if ($columnGap - 20 -ge $minimumColumnGap) { $columnGap -= 20; continue }
            if ($null -ne $lastConflict) {
                throw "實體整組外框衝突：$($lastConflict.A.Id) [$($lastConflict.A.X1),$($lastConflict.A.Y1),$($lastConflict.A.X2),$($lastConflict.A.Y2)] 與 $($lastConflict.B.Id) [$($lastConflict.B.X1),$($lastConflict.B.Y1),$($lastConflict.B.X2),$($lastConflict.B.Y2)]，最近距離 $([Math]::Round($lastConflict.Distance,2))px；解開衝突需畫布寬 $width，超過 1800px。"
            }
            throw "全系統畫布寬 $width 超過 1800px；內容外框 [$minX,$minY,$maxX,$maxY]。"
        }
        $shiftX = 24 - $minX; $shiftY = 24 - $minY
        foreach ($shape in $shapes) { $shape.X += $shiftX; $shape.Y += $shiftY }
        foreach ($line in @($attributeConnections) + @($connections)) {
            $line.X1 += $shiftX; $line.X2 += $shiftX
            $line.Y1 += $shiftY; $line.Y2 += $shiftY
        }
        foreach ($label in $layout.Labels) {
            $label.X += $shiftX; $label.Y += $shiftY
            $label.Box.X1 += $shiftX; $label.Box.X2 += $shiftX
            $label.Box.Y1 += $shiftY; $label.Box.Y2 += $shiftY
        }
        $layout.Width = [int]$width
        $layout.Height = [int][Math]::Ceiling($maxY - $minY + 48)
        Assert-LayoutNumbers $layout
        Write-Host "全系統 Chen：$($layout.Width)×$($layout.Height)px；欄距=$columnGap；列距=$rowGap。"
        return $layout
    }
}

function Assert-ConceptualLayout {
    param($Layout, [string]$Language, [string]$Path)
    Set-StrictMode -Version Latest
    Assert-LayoutNumbers $Layout
    if ($Layout.Width -gt 1800) { throw "$Path 寬度 $($Layout.Width) 超過 1800px。" }
    $bounds = [System.Collections.Generic.List[object]]::new()
    foreach ($shape in $Layout.Shapes) {
        $label = Get-BilingualText $shape.Element $Language
        $estimate = Get-EstimatedTextWidth $label $shape.FontSize
        $inner = switch ($shape.Kind) { 'ellipse' { $shape.Width - 20 } 'diamond' { ($shape.Width * 0.72) - 20 } default { $shape.Width - 20 } }
        if ($estimate -gt $inner + 0.01) { throw "$Path 元素 $($shape.Id) 標籤放得下檢查失敗。" }
        $box = Get-SystemShapeBounds $shape
        if ($box.X1 -lt 0 -or $box.Y1 -lt 0 -or $box.X2 -gt $Layout.Width -or $box.Y2 -gt $Layout.Height) { throw "$Path 元素 $($shape.Id) 超出畫布。" }
        [void]$bounds.Add($box)
    }
    for ($i = 0; $i -lt $bounds.Count; $i++) {
        for ($j = $i + 1; $j -lt $bounds.Count; $j++) {
            if (Test-LayoutBoundsOverlap $bounds[$i] $bounds[$j]) { throw "$Path 元素外框重疊：$($bounds[$i].Id) 與 $($bounds[$j].Id)。" }
        }
    }
    foreach ($entry in $Layout.EntityShapes.GetEnumerator()) {
        $entity = $entry.Value
        $side = [string]$entity.Element.attributeSide
        $attrs = @($Layout.Shapes | Where-Object { $_.Id.StartsWith("attribute:$($entry.Key):") })
        $entityBox = Get-SystemShapeBounds $entity
        $axis = if ($side -eq 'top' -or $side -eq 'bottom') { 'Y' } else { 'X' }
        $centers = @($attrs | ForEach-Object { $_.$axis })
        if (($centers | Measure-Object -Maximum).Maximum - ($centers | Measure-Object -Minimum).Minimum -gt 0.5) {
            throw "$Path 實體 $($entry.Key) 屬性對齊檢查失敗。"
        }
        $ordered = if ($axis -eq 'Y') { @($attrs | Sort-Object X) } else { @($attrs | Sort-Object Y) }
        for ($i = 1; $i -lt $ordered.Count; $i++) {
            $gap = if ($axis -eq 'Y') {
                ($ordered[$i].X - $ordered[$i].Width / 2) - ($ordered[$i-1].X + $ordered[$i-1].Width / 2)
            } else {
                ($ordered[$i].Y - $ordered[$i].Height / 2) - ($ordered[$i-1].Y + $ordered[$i-1].Height / 2)
            }
            if ([Math]::Abs($gap - 12) -gt 1) { throw "$Path 實體 $($entry.Key) 相鄰屬性間距 $gap，預期 12px。" }
        }
        $distance = switch ($side) {
            'top' { $entityBox.Y1 - (($attrs | ForEach-Object { $_.Y + $_.Height / 2 } | Measure-Object -Maximum).Maximum) }
            'bottom' { (($attrs | ForEach-Object { $_.Y - $_.Height / 2 } | Measure-Object -Minimum).Minimum) - $entityBox.Y2 }
            'left' { $entityBox.X1 - (($attrs | ForEach-Object { $_.X + $_.Width / 2 } | Measure-Object -Maximum).Maximum) }
            'right' { (($attrs | ForEach-Object { $_.X - $_.Width / 2 } | Measure-Object -Minimum).Minimum) - $entityBox.X2 }
        }
        if ($distance -lt 28 -or $distance -gt 40) { throw "$Path 實體 $($entry.Key) 屬性最近距離 $distance 不在 28～40px。" }
        foreach ($attr in $attrs) {
            $attrBox = Get-SystemShapeBounds $attr
            if (($side -eq 'top' -and $attrBox.Y2 -ge $entityBox.Y1) -or
                ($side -eq 'bottom' -and $attrBox.Y1 -le $entityBox.Y2) -or
                ($side -eq 'left' -and $attrBox.X2 -ge $entityBox.X1) -or
                ($side -eq 'right' -and $attrBox.X1 -le $entityBox.X2)) {
                throw "$Path 屬性 $($attr.Id) 不在規定的 $side 側。"
            }
        }
    }
    $conflict = Get-SystemGroupConflict $Layout $Layout.Diagram
    if ($null -ne $conflict) { throw "$Path 實體整組外框間距不足：$($conflict.A.Id) 與 $($conflict.B.Id)，距離 $($conflict.Distance)px。" }
    $segments = @((Get-SystemSegments $Layout).Items)
    foreach ($segment in $segments) {
        foreach ($box in $bounds) {
            if ($box.Id -eq $segment.Owner -or
                ($segment.PSObject.Properties['Attribute'] -and $box.Id -eq $segment.Attribute) -or
                ($segment.PSObject.Properties['Target'] -and $box.Id -eq $segment.Target)) { continue }
            if (Test-LineIntersectsBounds $segment $box) { throw "$Path 線 $($segment.Id) [$($segment.X1),$($segment.Y1),$($segment.X2),$($segment.Y2)] 穿過 $($box.Id) [$($box.X1),$($box.Y1),$($box.X2),$($box.Y2)]。" }
        }
    }
    for ($i = 0; $i -lt $segments.Count; $i++) {
        for ($j = $i + 1; $j -lt $segments.Count; $j++) {
            $a = $segments[$i]; $b = $segments[$j]
            if ($a.Id -eq $b.Id) { continue }
            if (Test-LineSegmentsIntersect $a.X1 $a.Y1 $a.X2 $a.Y2 $b.X1 $b.Y1 $b.X2 $b.Y2) {
                throw "$Path 全部線段交叉：$($a.Id) 與 $($b.Id)。"
            }
        }
    }
    for ($i = 0; $i -lt $Layout.Labels.Count; $i++) {
        $label = $Layout.Labels[$i]
        foreach ($box in $bounds) {
            if (Test-LayoutBoundsOverlap $label.Box $box) { throw "$Path 基數標籤 $($label.Id) 碰到 $($box.Id)。" }
        }
        foreach ($segment in $segments) {
            if ((Get-SegmentBoundsDistance $segment $label.Box) -lt 0.75) {
                throw "$Path 基數標籤 $($label.Id) 碰到線 $($segment.Id)。"
            }
        }
        for ($j = $i + 1; $j -lt $Layout.Labels.Count; $j++) {
            if (Test-LayoutBoundsOverlap $label.Box $Layout.Labels[$j].Box) { throw "$Path 基數標籤 $($label.Id) 碰到 $($Layout.Labels[$j].Id)。" }
        }
    }
    $content = @($bounds) + @($Layout.Labels | ForEach-Object { $_.Box })
    foreach ($segment in $segments) {
        $content += (New-LayoutBounds $segment.Id $segment.Id $segment.Id 'line' `
            ([Math]::Min($segment.X1,$segment.X2) - 0.75) ([Math]::Min($segment.Y1,$segment.Y2) - 0.75) `
            ([Math]::Max($segment.X1,$segment.X2) + 0.75) ([Math]::Max($segment.Y1,$segment.Y2) + 0.75))
    }
    $minX = ($content | Measure-Object X1 -Minimum).Minimum
    $minY = ($content | Measure-Object Y1 -Minimum).Minimum
    $maxX = ($content | Measure-Object X2 -Maximum).Maximum
    $maxY = ($content | Measure-Object Y2 -Maximum).Maximum
    foreach ($edge in @($minX, $minY, ($Layout.Width - $maxX), ($Layout.Height - $maxY))) {
        if ([Math]::Abs($edge - 24) -gt 1) { throw "$Path 畫布邊距 $edge 不在 24±1px。" }
    }
}

function New-ConceptualModelSvg {
    param($Diagram, $Layout, [string]$Language, [string]$Theme, [string]$Path)
    Assert-ConceptualLayout $Layout $Language $Path
    $palette = if ($Theme -eq 'dark') {
        @{ Background = '#111827'; Entity = '#24364b'; Relationship = '#473b25'; Attribute = '#1c2a3b'; Foreground = '#f4f7fb'; Line = '#a8bacd'; Border = '#98afc6' }
    } else {
        @{ Background = '#ffffff'; Entity = '#eef4f8'; Relationship = '#fff7df'; Attribute = '#ffffff'; Foreground = '#17212b'; Line = '#4a5560'; Border = '#4a5560' }
    }
    $title = if ($Language -eq 'en') { [string]$Diagram.nameEn } else { [string]$Diagram.title }
    $description = if ($Language -eq 'en') { [string]$Diagram.captionEn } else { [string]$Diagram.caption }
    $svg = New-Object System.Collections.Generic.List[string]
    [void]$svg.Add('<?xml version="1.0" encoding="UTF-8"?>')
    [void]$svg.Add("<svg xmlns=`"http://www.w3.org/2000/svg`" width=`"$($Layout.Width)`" height=`"$($Layout.Height)`" viewBox=`"0 0 $($Layout.Width) $($Layout.Height)`" role=`"img`" aria-labelledby=`"title description`">")
    [void]$svg.Add("  <title id=`"title`">$(ConvertTo-SvgText $title)</title>")
    [void]$svg.Add("  <desc id=`"description`">$(ConvertTo-SvgText $description)</desc>")
    [void]$svg.Add("  <rect x=`"0`" y=`"0`" width=`"$($Layout.Width)`" height=`"$($Layout.Height)`" fill=`"$($palette.Background)`"/>")
    foreach ($segment in $Layout.AttributeConnections) {
        [void]$svg.Add("  <line data-attribute-line=`"$($segment.Id)`" x1=`"$($segment.X1)`" y1=`"$($segment.Y1)`" x2=`"$($segment.X2)`" y2=`"$($segment.Y2)`" stroke=`"$($palette.Line)`" stroke-width=`"1.5`"/>")
    }
    foreach ($connection in $Layout.Connections) {
        $offsets = if ($connection.Participant.participation -eq 'total') { @(-3, 3) } else { @(0) }
        foreach ($offset in $offsets) {
            if ($connection.Straight) {
                $dx = $connection.X2 - $connection.X1; $dy = $connection.Y2 - $connection.Y1
                $length = [Math]::Sqrt($dx * $dx + $dy * $dy)
                $x1 = [Math]::Round($connection.X1 - $dy * $offset / $length, 2)
                $y1 = [Math]::Round($connection.Y1 + $dx * $offset / $length, 2)
                $x2 = [Math]::Round($connection.X2 - $dy * $offset / $length, 2)
                $y2 = [Math]::Round($connection.Y2 + $dx * $offset / $length, 2)
                [void]$svg.Add("  <line data-connection=`"$($connection.Id)`" x1=`"$x1`" y1=`"$y1`" x2=`"$x2`" y2=`"$y2`" stroke=`"$($palette.Line)`" stroke-width=`"1.5`"/>")
                continue
            }
            $portY = $connection.PortY + $offset; $trackX = $connection.TrackX + $offset; $endY = $connection.EndY + $offset
            [void]$svg.Add("  <path data-connection=`"$($connection.Id)`" d=`"M $($connection.PortX) $portY H $trackX V $endY H $($connection.EndX)`" fill=`"none`" stroke=`"$($palette.Line)`" stroke-width=`"1.5`"/>")
        }
    }
    foreach ($label in $Layout.Labels) {
        $box = $label.Box
        [void]$svg.Add("  <rect data-cardinality-label=`"$($label.Id)`" x=`"$($box.X1)`" y=`"$($box.Y1)`" width=`"$($label.Width)`" height=`"$($label.Height)`" fill=`"$($palette.Background)`"/>")
        [void]$svg.Add("  <text x=`"$($label.X)`" y=`"$($label.Y + 4.5)`" text-anchor=`"middle`" font-size=`"13`" fill=`"$($palette.Foreground)`">$($label.Connection.Participant.cardinality)</text>")
    }
    foreach ($shape in $Layout.Shapes) {
        $x = $shape.X - ($shape.Width / 2); $y = $shape.Y - ($shape.Height / 2)
        switch ($shape.Kind) {
            'rect' { [void]$svg.Add("  <rect data-element=`"$($shape.Id)`" x=`"$x`" y=`"$y`" width=`"$($shape.Width)`" height=`"$($shape.Height)`" fill=`"$($palette.Entity)`" stroke=`"$($palette.Border)`" stroke-width=`"1.5`"/>") }
            'ellipse' { [void]$svg.Add("  <ellipse data-element=`"$($shape.Id)`" cx=`"$($shape.X)`" cy=`"$($shape.Y)`" rx=`"$($shape.Width / 2)`" ry=`"$($shape.Height / 2)`" fill=`"$($palette.Attribute)`" stroke=`"$($palette.Border)`" stroke-width=`"1.5`"/>") }
            'diamond' {
                $points = "$($shape.X),$y $($shape.X + ($shape.Width / 2)),$($shape.Y) $($shape.X),$($shape.Y + ($shape.Height / 2)) $x,$($shape.Y)"
                [void]$svg.Add("  <polygon data-element=`"$($shape.Id)`" points=`"$points`" fill=`"$($palette.Relationship)`" stroke=`"$($palette.Border)`" stroke-width=`"1.5`"/>")
            }
        }
        $label = Get-BilingualText $shape.Element $Language
        $baseline = $shape.Y + ($shape.FontSize * 0.35)
        [void]$svg.Add("  <text x=`"$($shape.X)`" y=`"$baseline`" text-anchor=`"middle`" font-size=`"$($shape.FontSize)`" fill=`"$($palette.Foreground)`">$(ConvertTo-SvgText $label)</text>")
        if ($shape.PrimaryKey) {
            $lineWidth = Get-EstimatedTextWidth $label $shape.FontSize
            $underlineY = $baseline + 3; $x1 = $shape.X - ($lineWidth / 2); $x2 = $shape.X + ($lineWidth / 2)
            [void]$svg.Add("  <line data-primary-key=`"$($shape.Id)`" x1=`"$x1`" y1=`"$underlineY`" x2=`"$x2`" y2=`"$underlineY`" stroke=`"$($palette.Foreground)`" stroke-width=`"1`"/>")
        }
    }
    [void]$svg.Add('</svg>')
    return (($svg -join "`n") + "`n")
}

function New-RelationalSchemaSvg {
    param(
        [Parameter(Mandatory = $true)]$DatabaseModel,
        [Parameter(Mandatory = $true)]$Schema,
        [string]$Language
    )

    $tables = @($Schema.tables | ForEach-Object { [string]$_ })
    $tableSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $tableSet.UnionWith([string[]]$tables)
    $sortedMappings = @($DatabaseModel.Mappings | Where-Object { $tableSet.Contains($_.ChildTable) -and $tableSet.Contains($_.ParentTable) } | Sort-Object Constraint, Position)
    foreach ($mapping in $sortedMappings) {
        if ([string]::IsNullOrWhiteSpace($mapping.DeleteRule)) { throw "資料字典查不到外鍵 $($mapping.Constraint) 的 DELETE_RULE。" }
        if (-not $DatabaseModel.PrimaryKeys.Contains("$($mapping.ParentTable)|$($mapping.ParentColumn)")) {
            throw "外鍵 $($mapping.Constraint) 的目標 $($mapping.ParentTable).$($mapping.ParentColumn) 不是主鍵欄位。"
        }
    }
    $tableOrder = @(Get-TopologicalTableOrder -Tables $tables -Mappings $sortedMappings)
    $tableOrderIndex = @{}
    for ($i = 0; $i -lt $tableOrder.Count; $i++) { $tableOrderIndex[$tableOrder[$i]] = $i }
    $sortedMappings = @($sortedMappings | Sort-Object @{ Expression = { $tableOrderIndex[$_.ChildTable] } }, Constraint, Position)

    $auditColumns = @('IS_DELETED', 'DELETED_AT', 'DELETED_BY', 'CREATED_AT', 'CREATED_BY', 'UPDATED_AT', 'UPDATED_BY', 'ROW_VERSION')
    $identityColumns = @('NORMALIZED_NAME', 'NORMALIZED_USER_NAME', 'NORMALIZED_EMAIL', 'EMAIL_CONFIRMED', 'PASSWORD_HASH', 'SECURITY_STAMP', 'CONCURRENCY_STAMP', 'PHONE_NUMBER', 'PHONE_NUMBER_CONFIRMED', 'TWO_FACTOR_ENABLED', 'LOCKOUT_END', 'LOCKOUT_ENABLED', 'ACCESS_FAILED_COUNT')
    $displayColumns = @{}
    foreach ($table in $tableOrder) {
        $items = New-Object System.Collections.Generic.List[object]
        $hasAudit = $false; $hasIdentity = $false
        foreach ($column in $DatabaseModel.Columns[$table]) {
            $summary = $null
            if ($auditColumns -contains $column) { $summary = 'audit'; $hasAudit = $true }
            if (($table -eq 'IDENTITY_USERS' -or $table -eq 'IDENTITY_ROLES') -and $identityColumns -contains $column) { $summary = 'identity'; $hasIdentity = $true }
            if ($null -ne $summary) {
                $key = "$table|$column"
                if ($DatabaseModel.PrimaryKeys.Contains($key) -or $DatabaseModel.ForeignKeys.Contains($key)) {
                    throw "主鍵或外鍵 $table.$column 不得收進摘要欄位。"
                }
                continue
            }
            [void]$items.Add([pscustomobject]@{ Name = $column; Summary = $false; SummaryType = '' })
        }
        if ($hasAudit) { [void]$items.Add([pscustomobject]@{ Name = $(if ($Language -eq 'en') { '+ Audit fields' } else { '＋ 稽核欄位' }); Summary = $true; SummaryType = 'audit' }) }
        if ($hasIdentity) { [void]$items.Add([pscustomobject]@{ Name = $(if ($Language -eq 'en') { '+ Identity fields' } else { '＋ Identity 內建欄位' }); Summary = $true; SummaryType = 'identity' }) }
        $displayColumns[$table] = $items
    }

    $svgWidth = 1400; $laneSpacing = 12; $leftMargin = 24; $tableNameWidth = 190; $tableGap = 18
    $cellHeight = 34; $baseGap = 12; $endpointSpacing = 9; $interTableGap = 12
    $tableX = $leftMargin + ($sortedMappings.Count * $laneSpacing) + 16
    $fieldX = $tableX + $tableNameWidth + $tableGap
    $fieldAreaWidth = $svgWidth - $fieldX - 22
    $tableLayouts = @{}; $placements = @{}
    foreach ($table in $tableOrder) {
        $lineIndex = 0; $nextX = $fieldX
        $tablePlacements = New-Object System.Collections.Generic.List[object]
        foreach ($column in $displayColumns[$table]) {
            $cellWidth = [Math]::Max(92, [Math]::Ceiling((Get-EstimatedTextWidth $column.Name 12) + 24))
            if ($nextX -gt $fieldX -and ($nextX + $cellWidth) -gt ($fieldX + $fieldAreaWidth)) { $lineIndex++; $nextX = $fieldX }
            $placement = [pscustomobject]@{ Table = $table; Column = $column.Name; X = $nextX; Y = 0; Width = $cellWidth; Line = $lineIndex; Summary = $column.Summary; SummaryType = $column.SummaryType }
            [void]$tablePlacements.Add($placement)
            if (-not $column.Summary) { $placements["$table|$($column.Name)"] = $placement }
            $nextX += $cellWidth
        }
        $tableLayouts[$table] = [pscustomobject]@{ Placements = $tablePlacements; LineCount = $lineIndex + 1; StartY = 0 }
    }

    $gapEndpointCounts = @{}; $gapNextSlots = @{}
    for ($i = 0; $i -lt $sortedMappings.Count; $i++) {
        $mapping = $sortedMappings[$i]
        $source = $placements["$($mapping.ChildTable)|$($mapping.ChildColumn)"]; $target = $placements["$($mapping.ParentTable)|$($mapping.ParentColumn)"]
        if ($null -eq $source -or $null -eq $target) { throw "外鍵 $($mapping.Constraint) 的主鍵／外鍵欄位被收起或不存在。" }
        $sourceGapKey = "$($mapping.ChildTable)|$($source.Line)"; $targetGapKey = "$($mapping.ParentTable)|$($target.Line)"
        foreach ($gapKey in @($sourceGapKey, $targetGapKey)) {
            if (-not $gapEndpointCounts.ContainsKey($gapKey)) { $gapEndpointCounts[$gapKey] = 0 }
            $gapEndpointCounts[$gapKey]++
            if (-not $gapNextSlots.ContainsKey($gapKey)) { $gapNextSlots[$gapKey] = 0 }
        }
        $mapping | Add-Member -NotePropertyName Lane -NotePropertyValue $i -Force
        $mapping | Add-Member -NotePropertyName SourceGapKey -NotePropertyValue $sourceGapKey -Force
        $mapping | Add-Member -NotePropertyName TargetGapKey -NotePropertyValue $targetGapKey -Force
        $mapping | Add-Member -NotePropertyName SourceSlot -NotePropertyValue $gapNextSlots[$sourceGapKey] -Force
        $mapping | Add-Member -NotePropertyName TargetSlot -NotePropertyValue $gapNextSlots[$targetGapKey] -Force
        $gapNextSlots[$sourceGapKey]++; $gapNextSlots[$targetGapKey]++
    }
    $gapBottoms = @{}; $currentY = 28
    foreach ($table in $tableOrder) {
        $layout = $tableLayouts[$table]; $layout.StartY = $currentY
        for ($lineIndex = 0; $lineIndex -lt $layout.LineCount; $lineIndex++) {
            foreach ($placement in $layout.Placements | Where-Object { $_.Line -eq $lineIndex }) { $placement.Y = $currentY }
            $gapKey = "$table|$lineIndex"; $gapBottoms[$gapKey] = $currentY + $cellHeight
            $count = if ($gapEndpointCounts.ContainsKey($gapKey)) { $gapEndpointCounts[$gapKey] } else { 0 }
            $currentY += $cellHeight + $baseGap + ($count * $endpointSpacing)
        }
        $currentY += $interTableGap
    }

    $legendY = $currentY + 6; $svgHeight = $legendY + 150
    $monoFont = 'Consolas, Menlo, &quot;DejaVu Sans Mono&quot;, monospace'
    $sansFont = '&quot;Microsoft JhengHei&quot;, &quot;Noto Sans TC&quot;, &quot;PingFang TC&quot;, sans-serif'
    $lines = New-Object System.Collections.Generic.List[string]
    [void]$lines.Add('<?xml version="1.0" encoding="UTF-8"?>')
    [void]$lines.Add("<svg xmlns=`"http://www.w3.org/2000/svg`" width=`"$svgWidth`" height=`"$svgHeight`" viewBox=`"0 0 $svgWidth $svgHeight`" role=`"img`" aria-labelledby=`"title description`">")
    $title = if ($Language -eq 'en') { "MEDSUPPLY $($Schema.nameEn)" } else { "MEDSUPPLY $($Schema.title)" }
    $caption = if ($Language -eq 'en') { [string]$Schema.captionEn } else { [string]$Schema.caption }
    [void]$lines.Add("  <title id=`"title`">$(ConvertTo-SvgText $title)</title>")
    [void]$lines.Add("  <desc id=`"description`">$(ConvertTo-SvgText $caption)</desc>")
    [void]$lines.Add('  <defs><marker id="arrow" markerWidth="10" markerHeight="10" refX="9" refY="5" orient="auto" markerUnits="strokeWidth"><path d="M 0 0 L 10 5 L 0 10 z" fill="#40566d"/></marker></defs>')
    [void]$lines.Add("  <rect x=`"0`" y=`"0`" width=`"$svgWidth`" height=`"$svgHeight`" fill=`"#ffffff`"/>")
    foreach ($table in $tableOrder) {
        $layout = $tableLayouts[$table]
        [void]$lines.Add("  <g class=`"table`" data-table=`"$table`">")
        [void]$lines.Add("    <rect x=`"$tableX`" y=`"$($layout.StartY)`" width=`"$tableNameWidth`" height=`"$cellHeight`" fill=`"#e7eef6`" stroke=`"#6f8295`"/>")
        [void]$lines.Add("    <text x=`"$($tableX + [int]($tableNameWidth / 2))`" y=`"$($layout.StartY + 22)`" text-anchor=`"middle`" font-family=`"$monoFont`" font-size=`"13`" font-weight=`"700`" fill=`"#162536`">$table</text>")
        foreach ($placement in $layout.Placements) {
            $fill = if ($placement.Summary) { '#eef1f4' } else { '#ffffff' }
            [void]$lines.Add("    <rect x=`"$($placement.X)`" y=`"$($placement.Y)`" width=`"$($placement.Width)`" height=`"$cellHeight`" fill=`"$fill`" stroke=`"#8c9aa8`"/>")
            $textX = $placement.X + [int]($placement.Width / 2); $textY = $placement.Y + 22
            $columnKey = "$table|$($placement.Column)"; $isPrimaryKey = $DatabaseModel.PrimaryKeys.Contains($columnKey)
            if ($placement.Summary) {
                [void]$lines.Add("    <text x=`"$textX`" y=`"$textY`" text-anchor=`"middle`" font-family=`"$sansFont`" font-size=`"12`" font-style=`"italic`" fill=`"#4c5d6c`">$(ConvertTo-SvgText $placement.Column)</text>")
            }
            else {
                $weight = if ($isPrimaryKey) { '700' } else { '400' }
                [void]$lines.Add("    <text x=`"$textX`" y=`"$textY`" text-anchor=`"middle`" font-family=`"$monoFont`" font-size=`"12`" font-weight=`"$weight`" fill=`"#162536`">$($placement.Column)</text>")
                if ($isPrimaryKey) {
                    $underlineWidth = [Math]::Min($placement.Width - 18, $placement.Column.Length * 7)
                    [void]$lines.Add("    <line x1=`"$($textX - [int]($underlineWidth / 2))`" y1=`"$($placement.Y + 26)`" x2=`"$($textX + [int]($underlineWidth / 2))`" y2=`"$($placement.Y + 26)`" stroke=`"#162536`" stroke-width=`"1`"/>")
                }
            }
        }
        [void]$lines.Add('  </g>')
    }
    foreach ($mapping in $sortedMappings) {
        $source = $placements["$($mapping.ChildTable)|$($mapping.ChildColumn)"]; $target = $placements["$($mapping.ParentTable)|$($mapping.ParentColumn)"]
        $sourceX = $source.X + [int]($source.Width / 2); $targetX = $target.X + [int]($target.Width / 2)
        $sourceBottom = $source.Y + $cellHeight; $targetBottom = $target.Y + $cellHeight
        $sourceY = $gapBottoms[$mapping.SourceGapKey] + 7 + ($mapping.SourceSlot * $endpointSpacing)
        $targetY = $gapBottoms[$mapping.TargetGapKey] + 7 + ($mapping.TargetSlot * $endpointSpacing)
        $laneX = $leftMargin + ($mapping.Lane * $laneSpacing)
        switch ($mapping.DeleteRule) { 'CASCADE' { $dash = '' } 'NO ACTION' { $dash = ' stroke-dasharray="8 6"' } 'SET NULL' { $dash = ' stroke-dasharray="2 5"' } default { throw "外鍵 $($mapping.Constraint) 使用未支援的 DELETE_RULE：$($mapping.DeleteRule)。" } }
        $sourceLabel = "$($mapping.ChildTable).$($mapping.ChildColumn)"; $targetLabel = "$($mapping.ParentTable).$($mapping.ParentColumn)"
        [void]$lines.Add("  <g class=`"foreign-key`" data-constraint=`"$($mapping.Constraint)`" data-source=`"$sourceLabel`" data-target=`"$targetLabel`" data-delete-rule=`"$($mapping.DeleteRule)`"><title>$(ConvertTo-SvgText "$sourceLabel → $targetLabel ($($mapping.DeleteRule))")</title><path d=`"M $sourceX $sourceBottom V $sourceY H $laneX V $targetY H $targetX V $targetBottom`" fill=`"none`" stroke=`"#40566d`" stroke-width=`"1.5`"$dash marker-end=`"url(#arrow)`"/></g>")
    }
    $legendWidth = $svgWidth - $tableX - 22
    $legendText = if ($Language -eq 'en') {
        @('Solid: CASCADE    Dashed: NO ACTION / RESTRICT    Dotted: SET NULL    Underline: primary key',
          'Audit and Identity bookkeeping fields are summarized.',
          'Excluded unused tables: IDENTITY_USER_CLAIMS, IDENTITY_USER_LOGINS,',
          'IDENTITY_USER_TOKENS, IDENTITY_ROLE_CLAIMS')
    } else {
        @('實線：CASCADE　虛線：NO ACTION / RESTRICT　點線：SET NULL　底線：主鍵',
          '稽核與 Identity 內建簿記欄位以摘要格表示。',
          '未使用資料表：IDENTITY_USER_CLAIMS、IDENTITY_USER_LOGINS、',
          'IDENTITY_USER_TOKENS、IDENTITY_ROLE_CLAIMS')
    }
    [void]$lines.Add("  <g class=`"legend`" font-family=`"$sansFont`" fill=`"#263746`">")
    [void]$lines.Add("    <rect x=`"$tableX`" y=`"$legendY`" width=`"$legendWidth`" height=`"130`" rx=`"6`" fill=`"#f7f9fb`" stroke=`"#c6d0da`"/>")
    for ($i = 0; $i -lt $legendText.Count; $i++) {
        $estimated = Get-EstimatedTextWidth $legendText[$i] 12
        if ($estimated -gt $legendWidth - 36) { throw "關聯綱目圖例第 $($i + 1) 行標籤放得下檢查失敗：估計寬 $([Math]::Round($estimated, 1))px，形狀內寬 $($legendWidth - 36)px。" }
        [void]$lines.Add("    <text x=`"$($tableX + 18)`" y=`"$($legendY + 25 + ($i * 27))`" font-size=`"12`">$(ConvertTo-SvgText $legendText[$i])</text>")
    }
    [void]$lines.Add('  </g>')
    [void]$lines.Add('</svg>')
    return (($lines -join "`n") + "`n")
}

function Set-RelationalTheme {
    param([string]$Svg, [string]$Theme)
    if ($Theme -ne 'dark') { return $Svg }
    $pairs = @(
        @('#ffffff','#111827'), @('#e7eef6','#24364b'), @('#6f8295','#a8bacd'),
        @('#8c9aa8','#98afc6'), @('#162536','#f4f7fb'), @('#eef1f4','#2a3544'),
        @('#4c5d6c','#d8e2ed'), @('#40566d','#abc3dc'), @('#263746','#e1eaf3'),
        @('#f7f9fb','#1c2a3b'), @('#c6d0da','#98afc6')
    )
    for ($i = 0; $i -lt $pairs.Count; $i++) { $Svg = $Svg.Replace($pairs[$i][0], "__PALETTE_$i`__") }
    for ($i = 0; $i -lt $pairs.Count; $i++) { $Svg = $Svg.Replace("__PALETTE_$i`__", $pairs[$i][1]) }
    return $Svg
}

function Assert-SvgVariant {
    param([string]$Svg, [string]$Path, [string]$Language, [string]$Theme, [int]$WidthLimit)
    $xml = [xml]$Svg
    $root = $xml.DocumentElement
    if ([int]$root.GetAttribute('width') -gt $WidthLimit) { throw "$Path 寬度超過 ${WidthLimit}px。" }
    $ns = [System.Xml.XmlNamespaceManager]::new($xml.NameTable)
    $ns.AddNamespace('s', 'http://www.w3.org/2000/svg')
    foreach ($node in $xml.SelectNodes('//s:text', $ns)) {
        $size = $node.GetAttribute('font-size')
        if ([string]::IsNullOrWhiteSpace($size) -or [double]$size -lt 12) { throw "$Path 文字 $($node.InnerText) 字級小於 12px。" }
    }
    if ($Language -eq 'en' -and $Svg -match '[\u2e80-\u9fff\uac00-\ud7af\uf900-\ufaff\uff00-\uffef]') { throw "$Path 英文版含 CJK 字元。" }
    if ($Theme -eq 'dark') {
        foreach ($color in @('#ffffff','#eef4f8','#fff7df','#17212b','#4a5560','#e7eef6','#6f8295','#8c9aa8','#162536','#eef1f4','#4c5d6c','#40566d','#263746','#f7f9fb','#c6d0da')) {
            if ($Svg.Contains($color)) { throw "$Path 深色版不含亮色色碼檢查失敗：$color。" }
        }
    }
    foreach ($tableGroup in $xml.SelectNodes('//s:g[@class="table"]', $ns)) {
        $lastRectangle = $null
        foreach ($child in $tableGroup.ChildNodes) {
            if ($child.LocalName -eq 'rect') { $lastRectangle = $child }
            if ($child.LocalName -eq 'text') {
                $estimate = Get-EstimatedTextWidth $child.InnerText ([double]$child.GetAttribute('font-size'))
                $inside = [double]$lastRectangle.GetAttribute('width') - 20
                if ($estimate -gt $inside) { throw "$Path 資料表 $($tableGroup.GetAttribute('data-table')) 欄位 $($child.InnerText) 標籤放得下檢查失敗：估計寬 $([Math]::Round($estimate,1))px，形狀內寬 ${inside}px。" }
            }
        }
    }
}

function New-ModuleSvg {
    param($Diagram, [string]$Key, [string]$Language, [string]$Theme, [string]$Path)
    $sourcePath = Join-Path $repoRoot "scripts/diagram-templates/er-$Key.svg"
    $source = [System.IO.File]::ReadAllText($sourcePath, [System.Text.Encoding]::UTF8)
    $sourceXml = [xml]$source
    $sourceNs = [System.Xml.XmlNamespaceManager]::new($sourceXml.NameTable)
    $sourceNs.AddNamespace('s', 'http://www.w3.org/2000/svg')
    foreach ($element in @($Diagram.entities) + @($Diagram.relationships)) {
        if ($null -eq $element.x -or $null -eq $element.y) { throw "$Path 元素 $($element.id) 缺少原圖座標。" }
        $kind = if (@($Diagram.entities | Where-Object { $_.id -eq $element.id }).Count) { 'entity' } else { 'relationship' }
        $shape = $sourceXml.SelectSingleNode("//*[@data-element='$kind`:$($element.id)']", $sourceNs)
        if ($null -eq $shape) { throw "$Path 原圖缺少 $kind`:$($element.id)。" }
        $sourceX = if ($kind -eq 'entity') { [double]$shape.GetAttribute('x') + [double]$shape.GetAttribute('width') / 2 } else { [double](($shape.GetAttribute('points') -split '[ ,]')[0]) }
        $sourceY = if ($kind -eq 'entity') { [double]$shape.GetAttribute('y') + [double]$shape.GetAttribute('height') / 2 } else { [double](($shape.GetAttribute('points') -split '[ ,]')[1]) + [double]$element.height / 2 }
        if ($sourceX -ne [double]$element.x -or $sourceY -ne [double]$element.y) { throw "$Path 原圖 $($element.id) 座標與規格不符。" }
        foreach ($attribute in $element.attributes) {
            if ($null -eq $attribute.x -or $null -eq $attribute.y) { throw "$Path 屬性 $($attribute.id) 缺少原圖座標。" }
            $attributeId = "attribute:$($element.id):$($attribute.id)"
            $node = $sourceXml.SelectSingleNode("//*[@data-element='$attributeId']", $sourceNs)
            if ($null -eq $node -or [double]$node.GetAttribute('cx') -ne [double]$attribute.x -or [double]$node.GetAttribute('cy') -ne [double]$attribute.y) { throw "$Path 原圖 $attributeId 座標與規格不符。" }
            $label = @($sourceXml.SelectNodes('/s:svg/s:g[last()]/s:text', $sourceNs) | Where-Object { $_.InnerText -eq [string]$attribute.name -and [double]$_.GetAttribute('x') -eq [double]$attribute.x })
            if ($label.Count -eq 0) { throw "$Path 原圖 $attributeId 缺少標籤 $($attribute.name)。" }
        }
    }
    if ($Language -eq 'zh' -and $Theme -eq 'light') { return $source }
    if ($Language -eq 'zh') {
        return $source.Replace('#ffffff', '#111827').Replace('#eef4f8', '#24364b').Replace('#fff7df', '#473b25').Replace('#4a5560', '#a8bacd').Replace('#17212b', '#f4f7fb')
    }
    $xml = [xml]$source
    $nsUri = 'http://www.w3.org/2000/svg'
    $ns = [System.Xml.XmlNamespaceManager]::new($xml.NameTable); $ns.AddNamespace('s', $nsUri)
    $xml.SelectSingleNode('/s:svg/s:title', $ns).InnerText = [string]$Diagram.nameEn
    $xml.SelectSingleNode('/s:svg/s:desc', $ns).InnerText = [string]$Diagram.captionEn
    $root = $xml.DocumentElement
    if ($Key -eq 'identity') { $root.SetAttribute('width', '1020'); $root.SetAttribute('viewBox', '0 0 1020 900'); $root.SelectSingleNode('s:rect', $ns).SetAttribute('width', '1020') }
    $shapes = @{}; $bounds = [System.Collections.Generic.List[object]]::new()
    $texts = @($xml.SelectNodes('/s:svg/s:g[last()]/s:text', $ns))
    $labelIndex = 0
    foreach ($element in @($Diagram.entities) + @($Diagram.relationships)) {
        $isEntity = @($Diagram.entities | Where-Object { $_.id -eq $element.id }).Count -gt 0
        $id = if ($isEntity) { "entity:$($element.id)" } else { "relationship:$($element.id)" }
        $kind = if ($isEntity) { 'rect' } else { 'diamond' }
        $font = 15
        $estimate = Get-EstimatedTextWidth $element.nameEn $font
        $width = if ($isEntity) { [Math]::Max([double]$element.width, [Math]::Ceiling($estimate + 20)) } else { [Math]::Max([double]$element.width, [Math]::Ceiling(($estimate + 20) / 0.72)) }
        $shape = [pscustomobject]@{ Id = $id; X = [double]$element.x; Y = [double]$element.y; Width = $width; Height = [double]$element.height; Kind = $kind; DisplayName = $id }
        $shapes[$id] = $shape
        $node = $xml.SelectSingleNode("//*[@data-element='$id']", $ns)
        if ($isEntity) { $node.SetAttribute('x', [string]($shape.X - $width / 2)); $node.SetAttribute('width', [string]$width) }
        else {
            $half = $width / 2; $halfHeight = $shape.Height / 2
            $node.SetAttribute('points', "$($shape.X),$($shape.Y - $halfHeight) $($shape.X + $half),$($shape.Y) $($shape.X),$($shape.Y + $halfHeight) $($shape.X - $half),$($shape.Y)")
        }
        $texts[$labelIndex].InnerText = [string]$element.nameEn; $labelIndex++
        [void]$bounds.Add((New-LayoutBounds $id $id $id $kind ($shape.X - $width / 2) ($shape.Y - $shape.Height / 2) ($shape.X + $width / 2) ($shape.Y + $shape.Height / 2)))
        foreach ($attribute in $element.attributes) {
            $attrId = "attribute:$($element.id):$($attribute.id)"
            $x = [double]$attribute.x; $y = [double]$attribute.y
            if ($element.id -eq 'DEPARTMENT' -and $attribute.id -eq 'DEPARTMENT_CODE') { $y -= 40 }
            if ($Key -eq 'requisition' -and $attribute.id -eq 'REQUISITION_NO') { $y -= 40 }
            if ($Key -eq 'identity' -and $attribute.id -eq 'AUDIT_LOG_ID') { $y += 80 }
            if ($Key -eq 'identity' -and $attribute.id -eq 'DEPARTMENT_ID') { $x += 1 }
            $fontSize = if ($attribute.id -eq 'LOCATION_CODE') { 12 } else { 13 }
            $estimate = Get-EstimatedTextWidth $attribute.nameEn $fontSize
            $padding = if ($attribute.id -eq 'LOCATION_CODE') { 10 } else { 20 }
            $rx = [Math]::Max([double]$attribute.rx, [Math]::Ceiling(($estimate + $padding) / 2))
            $attrShape = [pscustomobject]@{ Id = $attrId; X = $x; Y = $y; Rx = $rx; Ry = [double]$attribute.ry; Width = (2 * $rx); Height = (2 * [double]$attribute.ry); Kind = 'ellipse'; DisplayName = $attrId }
            $shapes[$attrId] = $attrShape
            $node = $xml.SelectSingleNode("//*[@data-element='$attrId']", $ns)
            $node.SetAttribute('cx', [string]$x); $node.SetAttribute('cy', [string]$y); $node.SetAttribute('rx', [string]$rx)
            $texts[$labelIndex].InnerText = [string]$attribute.nameEn
            $texts[$labelIndex].SetAttribute('font-size', [string]$fontSize)
            $texts[$labelIndex].SetAttribute('x', [string]$x); $texts[$labelIndex].SetAttribute('y', [string]($y + 4.55)); $labelIndex++
            [void]$bounds.Add((New-LayoutBounds $attrId $attrId $attrId 'ellipse' ($x - $rx) ($y - $attrShape.Ry) ($x + $rx) ($y + $attrShape.Ry)))
        }
    }
    $width = [double]$root.GetAttribute('width'); $height = [double]$root.GetAttribute('height')
    foreach ($box in $bounds) {
        if ($box.X1 -lt 0 -or $box.X2 -gt $width -or $box.Y1 -lt 0 -or $box.Y2 -gt $height) { throw "$Path 元素 $($box.Id) 超出畫布：$($box.X1),$($box.Y1),$($box.X2),$($box.Y2)。" }
    }
    for ($i = 0; $i -lt $bounds.Count; $i++) {
        for ($j = $i + 1; $j -lt $bounds.Count; $j++) {
            if (Test-LayoutBoundsOverlap $bounds[$i] $bounds[$j]) { throw "$Path 元素外框重疊：$($bounds[$i].Id) 與 $($bounds[$j].Id)。" }
        }
    }
    $lineGroup = $xml.SelectSingleNode('/s:svg/s:g[1]', $ns); $lineGroup.RemoveAll()
    $lineGroup.SetAttribute('fill', 'none'); $lineGroup.SetAttribute('stroke', '#4a5560'); $lineGroup.SetAttribute('stroke-width', '1.5')
    $lines = [System.Collections.Generic.List[object]]::new()
    foreach ($element in @($Diagram.entities) + @($Diagram.relationships)) {
        $ownerId = if (@($Diagram.entities | Where-Object { $_.id -eq $element.id }).Count) { "entity:$($element.id)" } else { "relationship:$($element.id)" }
        foreach ($attribute in $element.attributes) {
            $attrId = "attribute:$($element.id):$($attribute.id)"
            $start = Get-ShapeBoundaryPoint $shapes[$ownerId] $shapes[$attrId].X $shapes[$attrId].Y
            $end = Get-ShapeBoundaryPoint $shapes[$attrId] $shapes[$ownerId].X $shapes[$ownerId].Y
            [void]$lines.Add([pscustomobject]@{ Id = "$ownerId/$attrId"; Owner = $ownerId; Target = $attrId; X1 = $start.X; Y1 = $start.Y; X2 = $end.X; Y2 = $end.Y })
        }
    }
    foreach ($relationship in $Diagram.relationships) {
        $relId = "relationship:$($relationship.id)"
        foreach ($participant in $relationship.participants) {
            $entityId = "entity:$($participant.entity)"
            $start = Get-ShapeBoundaryPoint $shapes[$entityId] $shapes[$relId].X $shapes[$relId].Y
            $end = Get-ShapeBoundaryPoint $shapes[$relId] $shapes[$entityId].X $shapes[$entityId].Y
            $offsets = if ($participant.participation -eq 'total') { @(-3, 3) } else { @(0) }
            foreach ($offset in $offsets) {
                $dx = $end.X - $start.X; $dy = $end.Y - $start.Y; $length = [Math]::Sqrt($dx * $dx + $dy * $dy)
                [void]$lines.Add([pscustomobject]@{ Id = "$relId/$entityId/$offset"; Owner = $relId; Target = $entityId; X1 = ($start.X - $dy * $offset / $length); Y1 = ($start.Y + $dx * $offset / $length); X2 = ($end.X - $dy * $offset / $length); Y2 = ($end.Y + $dx * $offset / $length) })
            }
        }
    }
    foreach ($line in $lines) {
        foreach ($box in $bounds) {
            if ($box.Id -eq $line.Owner -or $box.Id -eq $line.Target) { continue }
            if (Test-LineIntersectsBounds $line $box) { throw "$Path 連線 $($line.Id) 穿過 $($box.Id)。" }
        }
        $node = $xml.CreateElement('line', $nsUri)
        $node.SetAttribute('data-name', $line.Id)
        foreach ($axis in @('X1','Y1','X2','Y2')) { $node.SetAttribute($axis.ToLowerInvariant(), [string][Math]::Round($line.$axis, 2)) }
        [void]$lineGroup.AppendChild($node)
    }
    foreach ($line in @($xml.SelectNodes('/s:svg/s:g[last()]/s:line[@data-primary-key]', $ns))) { [void]$line.ParentNode.RemoveChild($line) }
    foreach ($element in $Diagram.entities) {
        foreach ($attribute in @($element.attributes | Where-Object { $_.primaryKey })) {
            $attrId = "attribute:$($element.id):$($attribute.id)"; $shape = $shapes[$attrId]
            $size = Get-EstimatedTextWidth $attribute.nameEn 13
            $node = $xml.CreateElement('line', $nsUri); $node.SetAttribute('data-primary-key', $attrId)
            $node.SetAttribute('x1', [string][Math]::Round($shape.X - $size / 2, 2)); $node.SetAttribute('x2', [string][Math]::Round($shape.X + $size / 2, 2))
            $node.SetAttribute('y1', [string]($shape.Y + 7.55)); $node.SetAttribute('y2', [string]($shape.Y + 7.55))
            $node.SetAttribute('stroke', '#17212b'); $node.SetAttribute('stroke-width', '1')
            [void]$xml.SelectSingleNode('/s:svg/s:g[last()]', $ns).AppendChild($node)
        }
    }
    $result = $xml.OuterXml
    if ($Theme -eq 'dark') { $result = $result.Replace('#ffffff', '#111827').Replace('#eef4f8', '#24364b').Replace('#fff7df', '#473b25').Replace('#4a5560', '#a8bacd').Replace('#17212b', '#f4f7fb') }
    return $result + "`n"
}

function Test-ByteArrayEqual {
    param([byte[]]$Left, [byte[]]$Right)
    if ($Left.Length -ne $Right.Length) { return $false }
    for ($i = 0; $i -lt $Left.Length; $i++) {
        if ($Left[$i] -ne $Right[$i]) { return $false }
    }
    return $true
}

function Get-MermaidLineLabels {
    param([string]$Text)

    $labels = @{}
    $currentTable = $null
    foreach ($line in ($Text -split "`r?`n")) {
        if ($line -match '^    ([A-Z0-9_]+) \{$') {
            $currentTable = $matches[1]
        }
        elseif ($line -match '^        \S+ ([A-Z0-9_]+)(?: (?:PK|FK|PK, FK))?$') {
            if ($null -ne $currentTable) {
                $labels[$line] = "$currentTable.$($matches[1])"
            }
        }
        elseif ($line -match '^    ([A-Z0-9_]+) (?:\|\||o\|)--o\{ ([A-Z0-9_]+) : ') {
            $labels[$line] = "關係 $($matches[1]) -> $($matches[2])"
        }
        elseif (-not [string]::IsNullOrWhiteSpace($line)) {
            $labels[$line] = '檔案標頭或 Mermaid 結構'
        }
    }
    return $labels
}

function Get-ReadmeWithDiagram {
    param([string]$ReadmeText, [string]$DiagramBody)

    $pattern = '(?s)(<!-- ER-DIAGRAM:BEGIN[^>]*-->\r?\n).*?(<!-- ER-DIAGRAM:END -->)'
    $match = [regex]::Match($ReadmeText, $pattern)
    if (-not $match.Success) {
        throw "在 $ReadmePath 找不到 ER-DIAGRAM:BEGIN / ER-DIAGRAM:END 標記，無法同步實體關係圖。"
    }

    $replacement = $match.Groups[1].Value + $DiagramBody + $match.Groups[2].Value
    return $ReadmeText.Substring(0, $match.Index) + $replacement + $ReadmeText.Substring($match.Index + $match.Length)
}

function Write-DriftDiff {
    param([string]$ExistingText, [string]$ExpectedText)

    $existingLabels = Get-MermaidLineLabels $ExistingText
    $expectedLabels = Get-MermaidLineLabels $ExpectedText
    $existingLines = @($ExistingText -split "`r?`n" | Where-Object { $_ -ne '' })
    $expectedLines = @($ExpectedText -split "`r?`n" | Where-Object { $_ -ne '' })
    $differences = Compare-Object -ReferenceObject $existingLines -DifferenceObject $expectedLines

    Write-Host 'ER 圖漂移：資料字典產生的內容與 docs/diagrams/schema.mmd 不一致。' -ForegroundColor Red
    Write-Host '不一致的資料表／欄位／關係：' -ForegroundColor Yellow
    foreach ($difference in $differences) {
        if ($difference.SideIndicator -eq '=>') {
            $label = $expectedLabels[$difference.InputObject]
            Write-Host "  缺少或已過期：[$label] $($difference.InputObject)"
        }
        else {
            $label = $existingLabels[$difference.InputObject]
            Write-Host "  多出或已過期：[$label] $($difference.InputObject)"
        }
    }
}

try {
    $docker = Get-DockerExecutable
    $password = Get-AppDatabasePassword
    $dictionaryRows = Invoke-OracleDictionaryQuery -Docker $docker -Password $password -Container $ContainerName
    $specification = Get-ConceptualModelSpecification
    $databaseModel = Get-DatabaseModel -DictionaryRows $dictionaryRows
    Assert-TableCoverage -Specification $specification -DatabaseModel $databaseModel
    Assert-ConceptualNames $specification.conceptualDiagrams.system
    Assert-ConceptualNames $specification.conceptualDiagrams.requisition
    Assert-ConceptualNames $specification.conceptualDiagrams.identity
    $diagram = New-MermaidDiagram -DictionaryRows $dictionaryRows
    $utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
    $expectedArtifacts = [ordered]@{}
    $expectedArtifacts[$OutputPath] = $utf8WithoutBom.GetBytes($diagram)
    $conceptual = $specification.conceptualDiagrams.system
    $layout = Get-DirectConceptualLayout -Diagram $conceptual
    foreach ($language in @('zh', 'en')) {
        foreach ($theme in @('light', 'dark')) {
            $conceptualPath = "docs/diagrams/er-chen-$language-$theme.svg"
            $relationalPath = "docs/diagrams/relational-schema-$language-$theme.svg"
            $conceptualSvg = New-ConceptualModelSvg -Diagram $conceptual -Layout $layout -Language $language -Theme $theme -Path $conceptualPath
            $relationalSvg = New-RelationalSchemaSvg -DatabaseModel $databaseModel -Schema $specification.relationalSchemas.system -Language $language
            $relationalSvg = Set-RelationalTheme $relationalSvg $theme
            Assert-SvgVariant $conceptualSvg $conceptualPath $language $theme 1800
            Assert-SvgVariant $relationalSvg $relationalPath $language $theme 1400
            $expectedArtifacts[$conceptualPath] = $utf8WithoutBom.GetBytes($conceptualSvg)
            $expectedArtifacts[$relationalPath] = $utf8WithoutBom.GetBytes($relationalSvg)
            foreach ($key in @('requisition', 'identity')) {
                $modulePath = "docs/diagrams/er-$key-$language-$theme.svg"
                $moduleSvg = New-ModuleSvg -Diagram $specification.conceptualDiagrams.$key -Key $key -Language $language -Theme $theme -Path $modulePath
                Assert-SvgVariant $moduleSvg $modulePath $language $theme 1800
                $expectedArtifacts[$modulePath] = $utf8WithoutBom.GetBytes($moduleSvg)
            }
        }
    }

    # README 內嵌的那一份只要 ```mermaid 圍欄本身，不要 .mmd 的檔案標頭註解。
    $fenceIndex = $diagram.IndexOf('```mermaid')
    if ($fenceIndex -lt 0) { throw '產生的內容裡找不到 mermaid 圍欄，無法同步 README。' }
    $diagramBody = $diagram.Substring($fenceIndex)

    $absoluteReadmePath = Join-Path $repoRoot $ReadmePath
    if (-not (Test-Path -LiteralPath $absoluteReadmePath)) {
        throw "找不到 $ReadmePath。"
    }
    $readmeBytes = [System.IO.File]::ReadAllBytes($absoluteReadmePath)
    $readmeText = [System.Text.Encoding]::UTF8.GetString($readmeBytes)
    $expectedReadmeText = Get-ReadmeWithDiagram -ReadmeText $readmeText -DiagramBody $diagramBody
    $expectedReadmeBytes = $utf8WithoutBom.GetBytes($expectedReadmeText)

    if ($Check) {
        foreach ($path in $expectedArtifacts.Keys) {
            $absolutePath = Join-Path $repoRoot $path
            if (-not (Test-Path -LiteralPath $absolutePath)) {
                Write-Host "資料模型圖漂移：找不到 $path。請先執行產生模式。" -ForegroundColor Red
                exit 1
            }
            $existingBytes = [System.IO.File]::ReadAllBytes($absolutePath)
            $existingNormalized = $utf8WithoutBom.GetBytes(([System.Text.Encoding]::UTF8.GetString($existingBytes) -replace "`r`n", "`n"))
            $expectedNormalized = $utf8WithoutBom.GetBytes(([System.Text.Encoding]::UTF8.GetString($expectedArtifacts[$path]) -replace "`r`n", "`n"))
            if (-not (Test-ByteArrayEqual -Left $existingNormalized -Right $expectedNormalized)) {
                if ($path -eq $OutputPath) {
                    $existingText = [System.Text.Encoding]::UTF8.GetString($existingBytes)
                    Write-DriftDiff -ExistingText $existingText -ExpectedText $diagram
                }
                else {
                    Write-Host "資料模型圖漂移：資料字典與圖面規格產生的內容與 $path 不一致。" -ForegroundColor Red
                    Write-Host '請執行產生模式（不加 -Check）重新同步。' -ForegroundColor Yellow
                }
                exit 1
            }
        }

        # ★ 第二份：README 內嵌的實體關係圖。schema.mmd 對了不代表 README 也對——
        #   README 是最多人看的那一份，它過期比 .mmd 過期更糟。
        $readmeNormalized = $utf8WithoutBom.GetBytes(($readmeText -replace "`r`n", "`n"))
        $expectedReadmeNormalized = $utf8WithoutBom.GetBytes(($expectedReadmeText -replace "`r`n", "`n"))
        if (-not (Test-ByteArrayEqual -Left $readmeNormalized -Right $expectedReadmeNormalized)) {
            Write-Host "ER 圖漂移：$ReadmePath 內嵌的實體關係圖與資料字典不一致。" -ForegroundColor Red
            Write-Host "請執行產生模式（不加 -Check）重新同步。" -ForegroundColor Yellow
            exit 1
        }

        Write-Host "資料模型圖均與圖面規格及資料字典一致：$($expectedArtifacts.Keys -join '、')、$ReadmePath" -ForegroundColor Green
        exit 0
    }

    foreach ($path in $expectedArtifacts.Keys) {
        $absolutePath = Join-Path $repoRoot $path
        $outputDirectory = Split-Path -Parent $absolutePath
        if (-not (Test-Path -LiteralPath $outputDirectory)) {
            New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
        }
        [System.IO.File]::WriteAllBytes($absolutePath, $expectedArtifacts[$path])
    }
    [System.IO.File]::WriteAllBytes($absoluteReadmePath, $expectedReadmeBytes)
    Write-Host "已從圖面規格與 Oracle 資料字典產生：$($expectedArtifacts.Keys -join '、')、$ReadmePath" -ForegroundColor Green
}
catch {
    Write-Host "產生資料模型圖失敗：$($_.Exception.Message)" -ForegroundColor Red
    Write-Host $_.InvocationInfo.PositionMessage -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor Red
    exit 1
}
