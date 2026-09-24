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

function Get-ConceptualLayout {
    param($Diagram, [bool]$PrimaryOnly)
    $entityFont = 15; $attributeFont = 13; $relationshipFont = 15
    $entities = @($Diagram.entities)
    $relationships = @($Diagram.relationships)
    $entityById = @{}
    $cellWidth = 220
    $cellHeight = if ($PrimaryOnly) { 160 } else { 190 }
    $gutterWidth = 140
    $cellById = @{}
    foreach ($entity in $entities) {
        if ($entityById.ContainsKey([string]$entity.id)) { throw "實體 id 重複：$($entity.id)。" }
        $entityById[[string]$entity.id] = $entity
        $row = [int]$entity.grid.row; $col = [int]$entity.grid.col
        if ($row -lt 0 -or $row -gt 3 -or $col -lt 0 -or $col -gt 2) { throw "實體 $($entity.id) 的 grid 超出 4×3。" }
        $slot = "$row|$col"
        if ($cellById.ContainsKey($slot)) { throw "格子 $slot 有多個實體。" }
        $cellById[$slot] = $entity.id
        if ($entity.attributeSide -ne 'bottom' -and $entity.attributeSide -ne 'top') { throw "實體 $($entity.id) 的屬性邊無效。" }
        $entityNeed = [Math]::Ceiling([Math]::Max((Get-EstimatedTextWidth $entity.name $entityFont), (Get-EstimatedTextWidth $entity.nameEn $entityFont)) + 28)
        $shown = @($entity.attributes | Where-Object { -not $PrimaryOnly -or $_.primaryKey })
        $attributeWidths = @($shown | ForEach-Object {
            $needed = [Math]::Ceiling([Math]::Max((Get-EstimatedTextWidth $_.name $attributeFont), (Get-EstimatedTextWidth $_.nameEn $attributeFont)) + 24)
            [Math]::Min(190, [Math]::Max(90, $needed))
        })
        $rowCount = [Math]::Min(2, $attributeWidths.Count)
        $firstRow = [int][Math]::Ceiling($attributeWidths.Count / 2.0)
        $firstNeed = 0; $secondNeed = 0
        for ($index = 0; $index -lt $attributeWidths.Count; $index++) {
            if ($index -lt $firstRow) { $firstNeed += $attributeWidths[$index] + 12 }
            else { $secondNeed += $attributeWidths[$index] + 12 }
        }
        $cellWidth = [Math]::Max($cellWidth, [Math]::Max($entityNeed + 36, [Math]::Max($firstNeed, $secondNeed) + 24))
    }
    $cellWidth = [int][Math]::Ceiling($cellWidth)
    $width = (3 * $cellWidth) + (4 * $gutterWidth)
    $grouped = @{}
    for ($row = 0; $row -lt 4; $row++) { $grouped[$row] = New-Object System.Collections.Generic.List[object] }
    foreach ($relationship in $relationships) {
        if (@($relationship.participants).Count -ne 2) { throw "關聯 $($relationship.id) 須恰有兩個參與實體。" }
        $leftEntity = $entityById[[string]$relationship.participants[0].entity]
        $rightEntity = $entityById[[string]$relationship.participants[1].entity]
        if ($null -eq $leftEntity -or $null -eq $rightEntity) { throw "關聯 $($relationship.id) 指向不存在的實體。" }
        $gap = [Math]::Max([int]$leftEntity.grid.row, [int]$rightEntity.grid.row)
        [void]$grouped[$gap].Add($relationship)
    }
    $rowTop = @(); $gapStart = @(); $gapHeight = @(); $cursor = 30
    for ($row = 0; $row -lt 4; $row++) {
        $rowTop += $cursor
        $cursor += $cellHeight
        $gapStart += $cursor
        $gapHeight += (50 + ($grouped[$row].Count * 130))
        $cursor += $gapHeight[$row]
    }
    $height = $cursor + 20
    $entityShapes = @{}; $attributeShapes = New-Object System.Collections.Generic.List[object]
    $shapes = New-Object System.Collections.Generic.List[object]
    foreach ($entity in $entities) {
        $col = [int]$entity.grid.col; $row = [int]$entity.grid.row
        $cellLeft = $gutterWidth + ($col * ($cellWidth + $gutterWidth))
        $cx = $cellLeft + ($cellWidth / 2); $cy = $rowTop[$row] + 45
        $labelWidth = [Math]::Max((Get-EstimatedTextWidth $entity.name $entityFont), (Get-EstimatedTextWidth $entity.nameEn $entityFont))
        $boxWidth = [Math]::Ceiling($labelWidth + 28)
        $record = [pscustomobject]@{ Id = "entity:$($entity.id)"; Kind = 'rect'; Element = $entity; X = $cx; Y = $cy; Width = $boxWidth; Height = 58; FontSize = 15; PrimaryKey = $false }
        $entityShapes[[string]$entity.id] = $record; [void]$shapes.Add($record)
        $shown = @($entity.attributes | Where-Object { -not $PrimaryOnly -or $_.primaryKey })
        $firstRow = [int][Math]::Ceiling($shown.Count / 2.0)
        for ($attrIndex = 0; $attrIndex -lt $shown.Count; $attrIndex++) {
            $attribute = $shown[$attrIndex]
            $attributeWidth = [Math]::Max((Get-EstimatedTextWidth $attribute.name 13), (Get-EstimatedTextWidth $attribute.nameEn 13))
            $diameter = [Math]::Min(190, [Math]::Max(90, [Math]::Ceiling($attributeWidth + 24)))
            $attrRow = if ($attrIndex -lt $firstRow) { 0 } else { 1 }
            $members = if ($attrRow -eq 0) { @($shown | Select-Object -First $firstRow) } else { @($shown | Select-Object -Skip $firstRow) }
            $localIndex = if ($attrRow -eq 0) { $attrIndex } else { $attrIndex - $firstRow }
            $widths = @($members | ForEach-Object {
                [Math]::Min(190, [Math]::Max(90, [Math]::Ceiling([Math]::Max((Get-EstimatedTextWidth $_.name 13), (Get-EstimatedTextWidth $_.nameEn 13)) + 24)))
            })
            $total = ($widths | Measure-Object -Sum).Sum + ([Math]::Max(0, $members.Count - 1) * 12)
            $x = $cx - ($total / 2) + ($diameter / 2)
            for ($prior = 0; $prior -lt $localIndex; $prior++) { $x += $widths[$prior] + 12 }
            $yOffset = if ($entity.attributeSide -eq 'bottom') { 110 + ($attrRow * 48) } else { -82 - ($attrRow * 48) }
            $shape = [pscustomobject]@{ Id = "attribute:$($entity.id):$($attribute.id)"; Kind = 'ellipse'; Element = $attribute; X = $x; Y = ($rowTop[$row] + $yOffset); Width = $diameter; Height = 44; FontSize = 13; PrimaryKey = [bool]$attribute.primaryKey }
            [void]$attributeShapes.Add($shape); [void]$shapes.Add($shape)
        }
    }
    $relationshipShapes = @{}; $relationshipAttrs = New-Object System.Collections.Generic.List[object]
    foreach ($gap in 0..3) {
        $index = 0
        foreach ($relationship in $grouped[$gap]) {
            $cols = @($relationship.participants | ForEach-Object { [int]$entityById[[string]$_.entity].grid.col })
            $col = [Math]::Min($cols[0], $cols[1])
            $x = $gutterWidth + ($col * ($cellWidth + $gutterWidth)) + ($cellWidth / 2)
            $y = $gapStart[$gap] + 60 + ($index * 130)
            $labelWidth = [Math]::Max((Get-EstimatedTextWidth $relationship.name 15), (Get-EstimatedTextWidth $relationship.nameEn 15))
            $diamondWidth = [Math]::Ceiling(($labelWidth + 24) / 0.72)
            $shape = [pscustomobject]@{ Id = "relationship:$($relationship.id)"; Kind = 'diamond'; Element = $relationship; X = $x; Y = $y; Width = $diamondWidth; Height = 72; FontSize = 15; PrimaryKey = $false }
            $relationshipShapes[[string]$relationship.id] = $shape; [void]$shapes.Add($shape)
            foreach ($attribute in $relationship.attributes) {
                $labelWidth = [Math]::Max((Get-EstimatedTextWidth $attribute.name 13), (Get-EstimatedTextWidth $attribute.nameEn 13))
                $diameter = [Math]::Min(190, [Math]::Max(90, [Math]::Ceiling($labelWidth + 24)))
                $attr = [pscustomobject]@{ Id = "attribute:$($relationship.id):$($attribute.id)"; Kind = 'ellipse'; Element = $attribute; X = $x; Y = ($y + 65); Width = $diameter; Height = 44; FontSize = 13; PrimaryKey = $false }
                [void]$relationshipAttrs.Add($attr); [void]$shapes.Add($attr)
            }
            $index++
        }
    }
    $gutterLanes = @(0, 0, 0, 0)
    $sidePorts = @{}
    $connections = New-Object System.Collections.Generic.List[object]
    foreach ($relationship in $relationships) {
        $first = $entityById[[string]$relationship.participants[0].entity]
        $second = $entityById[[string]$relationship.participants[1].entity]
        $firstCol = [int]$first.grid.col; $secondCol = [int]$second.grid.col
        $sides = if ($firstCol -lt $secondCol) { @('left', 'right') } elseif ($firstCol -gt $secondCol) { @('right', 'left') } else { @('left', 'right') }
        $diamond = $relationshipShapes[[string]$relationship.id]
        for ($participantIndex = 0; $participantIndex -lt 2; $participantIndex++) {
            $participant = $relationship.participants[$participantIndex]
            $entity = $entityShapes[[string]$participant.entity]
            $entitySpec = $entityById[[string]$participant.entity]
            $col = [int]$entitySpec.grid.col; $side = $sides[$participantIndex]
            $gutter = if ($side -eq 'left') { $col } else { $col + 1 }
            $lane = $gutterLanes[$gutter]; $gutterLanes[$gutter]++
            $gutterLeft = $gutter * ($cellWidth + $gutterWidth)
            $trackX = $gutterLeft + 20 + ($lane * 14)
            $portKey = "$($entity.Id)|$side"
            if (-not $sidePorts.ContainsKey($portKey)) { $sidePorts[$portKey] = 0 }
            $portNumber = $sidePorts[$portKey]; $sidePorts[$portKey]++
            $portY = $entity.Y + (($portNumber - 1) * 18)
            $portX = if ($side -eq 'left') { $entity.X - ($entity.Width / 2) } else { $entity.X + ($entity.Width / 2) }
            $diamondX = if ($trackX -lt $diamond.X) { $diamond.X - ($diamond.Width / 2) } else { $diamond.X + ($diamond.Width / 2) }
            $connection = [pscustomobject]@{ Id = "$($relationship.id):$($participant.entity)"; Relationship = $relationship; Participant = $participant; Entity = $entity; Diamond = $diamond; PortX = $portX; PortY = $portY; TrackX = $trackX; EndX = $diamondX; EndY = $diamond.Y; Gutter = $gutter; Lane = $lane }
            [void]$connections.Add($connection)
        }
    }
    for ($gutter = 0; $gutter -lt 4; $gutter++) {
        if ((20 + (($gutterLanes[$gutter] - 1) * 14) + 20) -gt $gutterWidth) { throw "垂直通道 $gutter 車道超出 ${gutterWidth}px。" }
    }
    $attributeConnections = New-Object System.Collections.Generic.List[object]
    foreach ($attribute in $attributeShapes) {
        $parts = $attribute.Id -split ':'
        $owner = $entityShapes[$parts[1]]
        $direction = if ($owner.Element.attributeSide -eq 'bottom') { 1 } else { -1 }
        [void]$attributeConnections.Add([pscustomobject]@{
            Id = "$($owner.Id):$($attribute.Id)"; Owner = $owner.Id; Attribute = $attribute.Id
            X1 = $owner.X; Y1 = $owner.Y + ($direction * $owner.Height / 2)
            X2 = $attribute.X; Y2 = $attribute.Y - ($direction * $attribute.Height / 2)
        })
    }
    foreach ($attribute in $relationshipAttrs) {
        $parts = $attribute.Id -split ':'
        $owner = $relationshipShapes[$parts[1]]
        [void]$attributeConnections.Add([pscustomobject]@{
            Id = "$($owner.Id):$($attribute.Id)"; Owner = $owner.Id; Attribute = $attribute.Id
            X1 = $owner.X; Y1 = $owner.Y + ($owner.Height / 2)
            X2 = $attribute.X; Y2 = $attribute.Y - ($attribute.Height / 2)
        })
    }
    return [pscustomobject]@{ Width = $width; Height = $height; CellWidth = $cellWidth; CellHeight = $cellHeight; GutterWidth = $gutterWidth; GapHeights = $gapHeight; GutterLanes = $gutterLanes; Shapes = $shapes; AttributeConnections = $attributeConnections; AttributeShapes = $attributeShapes; RelationshipAttributes = $relationshipAttrs; EntityShapes = $entityShapes; RelationshipShapes = $relationshipShapes; Connections = $connections; PrimaryOnly = $PrimaryOnly }
}

function Assert-ConceptualLayout {
    param($Layout, [string]$Language, [string]$Path)
    if ($Layout.Width -gt 1800) { throw "$Path 寬度 $($Layout.Width) 超過 1800px。" }
    $bounds = New-Object System.Collections.Generic.List[object]
    foreach ($shape in $Layout.Shapes) {
        $label = Get-BilingualText $shape.Element $Language
        $estimate = Get-EstimatedTextWidth $label $shape.FontSize
        $inner = switch ($shape.Kind) { 'ellipse' { $shape.Width - 20 } 'diamond' { ($shape.Width * 0.72) - 20 } default { $shape.Width - 20 } }
        if ($estimate -gt $inner + 0.01) { throw "$Path 元素 $($shape.Id) 標籤放得下檢查失敗：估計寬 $([Math]::Round($estimate, 1))px，形狀內寬 $([Math]::Round($inner, 1))px。" }
        $box = New-LayoutBounds $shape.Id $shape.Id $shape.Id $shape.Kind ($shape.X - ($shape.Width / 2)) ($shape.Y - ($shape.Height / 2)) ($shape.X + ($shape.Width / 2)) ($shape.Y + ($shape.Height / 2))
        if ($box.X1 -lt 0 -or $box.Y1 -lt 0 -or $box.X2 -gt $Layout.Width -or $box.Y2 -gt $Layout.Height) { throw "$Path 元素 $($shape.Id) 超出畫布。" }
        [void]$bounds.Add($box)
    }
    for ($i = 0; $i -lt $bounds.Count; $i++) {
        for ($j = $i + 1; $j -lt $bounds.Count; $j++) {
            if (Test-LayoutBoundsOverlap $bounds[$i] $bounds[$j]) { throw "$Path 元素外框重疊：$($bounds[$i].Id) 與 $($bounds[$j].Id)。" }
        }
    }
    $segments = New-Object System.Collections.Generic.List[object]
    foreach ($segment in $Layout.AttributeConnections) {
        [void]$segments.Add($segment)
        foreach ($box in $bounds) {
            if ($box.Id -eq $segment.Owner -or $box.Id -eq $segment.Attribute) { continue }
            if (Test-LineIntersectsBounds $segment $box) { throw "$Path 屬性線 $($segment.Id) 穿過 $($box.Id)。" }
        }
    }
    foreach ($connection in $Layout.Connections) {
        $offsets = if ($connection.Participant.participation -eq 'total') { @(-3, 3) } else { @(0) }
        foreach ($offset in $offsets) {
            $points = @(@($connection.PortX, ($connection.PortY + $offset)), @(($connection.TrackX + $offset), ($connection.PortY + $offset)), @(($connection.TrackX + $offset), ($connection.EndY + $offset)), @($connection.EndX, ($connection.EndY + $offset)))
            for ($index = 0; $index -lt 3; $index++) {
                $segment = [pscustomobject]@{ Id = $connection.Id; X1 = [double]$points[$index][0]; Y1 = [double]$points[$index][1]; X2 = [double]$points[$index + 1][0]; Y2 = [double]$points[$index + 1][1] }
                [void]$segments.Add($segment)
                foreach ($box in $bounds) {
                    if ($box.Id -eq $connection.Entity.Id -or $box.Id -eq $connection.Diamond.Id) { continue }
                    if (Test-LineIntersectsBounds $segment $box) { throw "$Path 連線 $($segment.Id) 穿過 $($box.Id)。" }
                }
            }
        }
    }
    for ($i = 0; $i -lt $segments.Count; $i++) {
        for ($j = $i + 1; $j -lt $segments.Count; $j++) {
            $a = $segments[$i]; $b = $segments[$j]
            if ($a.Id -eq $b.Id) { continue }
            if ($a.X1 -eq $a.X2 -and $b.X1 -eq $b.X2 -and $a.X1 -eq $b.X1 -and [Math]::Max([Math]::Min($a.Y1,$a.Y2),[Math]::Min($b.Y1,$b.Y2)) -lt [Math]::Min([Math]::Max($a.Y1,$a.Y2),[Math]::Max($b.Y1,$b.Y2))) { throw "$Path 垂直通道連線重疊：$($a.Id) 與 $($b.Id)。" }
            if ($a.Y1 -eq $a.Y2 -and $b.Y1 -eq $b.Y2 -and $a.Y1 -eq $b.Y1 -and [Math]::Max([Math]::Min($a.X1,$a.X2),[Math]::Min($b.X1,$b.X2)) -lt [Math]::Min([Math]::Max($a.X1,$a.X2),[Math]::Max($b.X1,$b.X2))) { throw "$Path 水平通道連線重疊：$($a.Id) 與 $($b.Id)。" }
        }
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
    if ($Layout.PrimaryOnly) { $description += $(if ($Language -eq 'en') { ' Only primary-key attributes are shown; other columns appear in the relational schema.' } else { ' 僅繪製主鍵屬性；其他欄位請見關聯綱目。' }) }
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
            $portY = $connection.PortY + $offset; $trackX = $connection.TrackX + $offset; $endY = $connection.EndY + $offset
            [void]$svg.Add("  <path data-connection=`"$($connection.Id)`" d=`"M $($connection.PortX) $portY H $trackX V $endY H $($connection.EndX)`" fill=`"none`" stroke=`"$($palette.Line)`" stroke-width=`"1.5`"/>")
        }
        $sign = if ($connection.TrackX -lt $connection.PortX) { -1 } else { 1 }
        $cardinalityX = $connection.PortX + ($sign * 18)
        [void]$svg.Add("  <text x=`"$cardinalityX`" y=`"$($connection.PortY - 9)`" text-anchor=`"middle`" font-size=`"13`" fill=`"$($palette.Foreground)`">$($connection.Participant.cardinality)</text>")
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
    if ($Layout.PrimaryOnly) {
        $note = if ($Language -eq 'en') { 'Only primary-key attributes are shown; other columns appear in the relational schema.' } else { '僅顯示各實體主鍵屬性；其他欄位見關聯綱目。' }
        $estimate = Get-EstimatedTextWidth $note 13
        if ($estimate -gt $Layout.Width - 40) { throw "$Path 圖說標籤放得下檢查失敗：估計寬 $([Math]::Round($estimate,1))px。" }
        [void]$svg.Add("  <text x=`"20`" y=`"$($Layout.Height - 12)`" font-size=`"13`" fill=`"$($palette.Foreground)`">$(ConvertTo-SvgText $note)</text>")
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
    $diagram = New-MermaidDiagram -DictionaryRows $dictionaryRows
    $utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
    $expectedArtifacts = [ordered]@{}
    $expectedArtifacts[$OutputPath] = $utf8WithoutBom.GetBytes($diagram)
    $conceptual = $specification.conceptualDiagrams.system
    $allLayout = Get-ConceptualLayout -Diagram $conceptual -PrimaryOnly $false
    $primaryOnly = $allLayout.Width -gt 1800
    $fallbackReason = if ($primaryOnly) { "自動格子寬度 $($allLayout.Width)px 超過 1800px" } else { '' }
    if (-not $primaryOnly) {
        try {
            Assert-ConceptualLayout $allLayout 'zh' 'docs/diagrams/er-chen-zh-light.svg'
            Assert-ConceptualLayout $allLayout 'en' 'docs/diagrams/er-chen-en-light.svg'
        }
        catch {
            $primaryOnly = $true
            $fallbackReason = $_.Exception.Message
        }
    }
    $layout = if ($primaryOnly) { Get-ConceptualLayout -Diagram $conceptual -PrimaryOnly $true } else { $allLayout }
    if ($primaryOnly) { Write-Host "完整屬性版未通過：$fallbackReason；改用主鍵屬性版 $($layout.Width)×$($layout.Height)px。" -ForegroundColor Yellow }
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
    exit 1
}
