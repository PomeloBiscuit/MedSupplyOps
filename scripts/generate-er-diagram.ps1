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
    foreach ($property in $Specification.relationalSchemas.psobject.Properties) {
        foreach ($table in $property.Value.tables) { [void]$drawn.Add([string]$table) }
    }
    $excluded = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($table in $Specification.excludedTables) { [void]$excluded.Add([string]$table) }

    $failures = New-Object System.Collections.Generic.List[string]
    foreach ($table in $DatabaseModel.Tables) {
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

function New-ConceptualModelSvg {
    param([Parameter(Mandatory = $true)]$Diagram)

    $width = [int]$Diagram.width
    $height = [int]$Diagram.height
    if ($width -gt 1000) { throw "概念模型 $($Diagram.title) 寬度 $width 超過 1000px。" }

    $shapes = @{}
    $bounds = New-Object System.Collections.Generic.List[object]
    $connections = New-Object System.Collections.Generic.List[object]
    $labels = New-Object System.Collections.Generic.List[object]

    function Add-ShapeAndLabel {
        param($Shape, [string]$Id, [string]$DisplayName, [string]$GroupId, [string]$Kind, [double]$FontSize)
        if ($shapes.ContainsKey($Id)) { throw "圖面元素 id 重複：$Id。" }
        $shapes[$Id] = $Shape
        $shapeBounds = if ($Kind -eq 'ellipse') {
            New-LayoutBounds $Id $DisplayName $GroupId $Kind ($Shape.X - $Shape.Rx) ($Shape.Y - $Shape.Ry) ($Shape.X + $Shape.Rx) ($Shape.Y + $Shape.Ry)
        }
        else {
            New-LayoutBounds $Id $DisplayName $GroupId $Kind ($Shape.X - ($Shape.Width / 2)) ($Shape.Y - ($Shape.Height / 2)) ($Shape.X + ($Shape.Width / 2)) ($Shape.Y + ($Shape.Height / 2))
        }
        [void]$bounds.Add($shapeBounds)
        $labelText = if ($DisplayName -match '「(.+)」') { $matches[1] } else { $DisplayName }
        $textWidth = [Math]::Max($FontSize, $labelText.Length * $FontSize)
        [void]$bounds.Add((New-LayoutBounds "$Id`:label" "$DisplayName 的文字標籤" $GroupId 'text' ($Shape.X - ($textWidth / 2)) ($Shape.Y - ($FontSize / 2)) ($Shape.X + ($textWidth / 2)) ($Shape.Y + ($FontSize / 2))))
        [void]$labels.Add([pscustomobject]@{ Shape = $Shape; Text = $DisplayName; FontSize = $FontSize; PrimaryKey = $false; GroupId = $GroupId })
    }

    foreach ($entity in $Diagram.entities) {
        $shape = [pscustomobject]@{ X = [double]$entity.x; Y = [double]$entity.y; Width = [double]$entity.width; Height = [double]$entity.height; Kind = 'rectangle'; DisplayName = "實體「$($entity.name)」" }
        Add-ShapeAndLabel $shape "entity:$($entity.id)" "實體「$($entity.name)」" "entity:$($entity.id)" 'rectangle' 15
        $labels[$labels.Count - 1].Text = [string]$entity.name
        foreach ($attribute in $entity.attributes) {
            $attributeShape = [pscustomobject]@{ X = [double]$attribute.x; Y = [double]$attribute.y; Rx = [double]$attribute.rx; Ry = [double]$attribute.ry; Kind = 'ellipse'; DisplayName = "屬性「$($attribute.name)」" }
            $attributeId = "attribute:$($entity.id):$($attribute.id)"
            Add-ShapeAndLabel $attributeShape $attributeId "屬性「$($attribute.name)」" $attributeId 'ellipse' 13
            $labels[$labels.Count - 1].Text = [string]$attribute.name
            $labels[$labels.Count - 1].PrimaryKey = [bool]$attribute.primaryKey
        }
    }
    foreach ($relationship in $Diagram.relationships) {
        $shape = [pscustomobject]@{ X = [double]$relationship.x; Y = [double]$relationship.y; Width = [double]$relationship.width; Height = [double]$relationship.height; Kind = 'diamond'; DisplayName = "關聯「$($relationship.name)」" }
        Add-ShapeAndLabel $shape "relationship:$($relationship.id)" "關聯「$($relationship.name)」" "relationship:$($relationship.id)" 'diamond' 15
        $labels[$labels.Count - 1].Text = [string]$relationship.name
        foreach ($attribute in $relationship.attributes) {
            $attributeShape = [pscustomobject]@{ X = [double]$attribute.x; Y = [double]$attribute.y; Rx = [double]$attribute.rx; Ry = [double]$attribute.ry; Kind = 'ellipse'; DisplayName = "屬性「$($attribute.name)」" }
            $attributeId = "attribute:$($relationship.id):$($attribute.id)"
            Add-ShapeAndLabel $attributeShape $attributeId "屬性「$($attribute.name)」" $attributeId 'ellipse' 13
            $labels[$labels.Count - 1].Text = [string]$attribute.name
        }
    }

    function Add-Connection {
        param([string]$Id, $From, $To, [string[]]$EndpointGroups, [string[]]$IgnoredGroups, [double]$Offset)
        $dx = $To.X - $From.X
        $dy = $To.Y - $From.Y
        $length = [Math]::Sqrt(($dx * $dx) + ($dy * $dy))
        $offsetX = if ($length -eq 0) { 0 } else { (-$dy / $length) * $Offset }
        $offsetY = if ($length -eq 0) { 0 } else { ($dx / $length) * $Offset }
        [void]$connections.Add([pscustomobject]@{
            Id = $Id; X1 = $From.X + $offsetX; Y1 = $From.Y + $offsetY
            X2 = $To.X + $offsetX; Y2 = $To.Y + $offsetY
            EndpointGroups = $EndpointGroups; IgnoredGroups = $IgnoredGroups
        })
    }

    foreach ($entity in $Diagram.entities) {
        $owner = $shapes["entity:$($entity.id)"]
        foreach ($attribute in $entity.attributes) {
            $attributeId = "attribute:$($entity.id):$($attribute.id)"
            $attributeShape = $shapes[$attributeId]
            $from = Get-ShapeBoundaryPoint $owner $attributeShape.X $attributeShape.Y
            $to = Get-ShapeBoundaryPoint $attributeShape $owner.X $owner.Y
            Add-Connection "屬性線 $($entity.name)—$($attribute.name)" $from $to @("entity:$($entity.id)", $attributeId) @() 0
        }
    }
    foreach ($relationship in $Diagram.relationships) {
        $relationshipId = "relationship:$($relationship.id)"
        $relationshipShape = $shapes[$relationshipId]
        foreach ($participant in $relationship.participants) {
            $entityRecord = @($Diagram.entities | Where-Object { $_.id -eq $participant.entity })
            if ($entityRecord.Count -ne 1) { throw "關聯 $($relationship.name) 找不到唯一的實體 $($participant.entity)。" }
            $entityId = "entity:$($participant.entity)"
            $entityShape = $shapes[$entityId]
            $from = Get-ShapeBoundaryPoint $entityShape $relationshipShape.X $relationshipShape.Y
            $to = Get-ShapeBoundaryPoint $relationshipShape $entityShape.X $entityShape.Y
            $cardinalityGroup = "cardinality:$($relationship.id):$($participant.entity)"
            if ($participant.participation -eq 'total') {
                Add-Connection "$($entityRecord[0].name)—$($relationship.name) 雙線 1" $from $to @($entityId, $relationshipId) @($cardinalityGroup) -3
                Add-Connection "$($entityRecord[0].name)—$($relationship.name) 雙線 2" $from $to @($entityId, $relationshipId) @($cardinalityGroup) 3
            }
            elseif ($participant.participation -eq 'partial') {
                Add-Connection "$($entityRecord[0].name)—$($relationship.name) 單線" $from $to @($entityId, $relationshipId) @($cardinalityGroup) 0
            }
            else { throw "關聯 $($relationship.name) 的參與類型 $($participant.participation) 不受支援。" }

            $dx = $to.X - $from.X
            $dy = $to.Y - $from.Y
            $length = [Math]::Sqrt(($dx * $dx) + ($dy * $dy))
            $cardinalityX = $from.X + ($dx * 0.28) + ((-$dy / $length) * 14)
            $cardinalityY = $from.Y + ($dy * 0.28) + (($dx / $length) * 14)
            [void]$bounds.Add((New-LayoutBounds $cardinalityGroup "基數 $($participant.cardinality)（$($entityRecord[0].name)—$($relationship.name)）" $cardinalityGroup 'text' ($cardinalityX - 7) ($cardinalityY - 8) ($cardinalityX + 7) ($cardinalityY + 8)))
            [void]$labels.Add([pscustomobject]@{ Shape = [pscustomobject]@{ X = $cardinalityX; Y = $cardinalityY }; Text = [string]$participant.cardinality; FontSize = 13; PrimaryKey = $false; GroupId = $cardinalityGroup })
        }
        foreach ($attribute in $relationship.attributes) {
            $attributeId = "attribute:$($relationship.id):$($attribute.id)"
            $attributeShape = $shapes[$attributeId]
            $from = Get-ShapeBoundaryPoint $relationshipShape $attributeShape.X $attributeShape.Y
            $to = Get-ShapeBoundaryPoint $attributeShape $relationshipShape.X $relationshipShape.Y
            Add-Connection "關聯屬性線 $($relationship.name)—$($attribute.name)" $from $to @($relationshipId, $attributeId) @() 0
        }
    }

    $layoutFailures = New-Object System.Collections.Generic.List[string]
    foreach ($item in $bounds) {
        if ($item.X1 -lt 0 -or $item.Y1 -lt 0 -or $item.X2 -gt $width -or $item.Y2 -gt $height) {
            [void]$layoutFailures.Add("元素 $($item.DisplayName) 超出 ${width}×${height} 畫布。")
        }
    }
    for ($i = 0; $i -lt $bounds.Count; $i++) {
        for ($j = $i + 1; $j -lt $bounds.Count; $j++) {
            if ($bounds[$i].GroupId -ne $bounds[$j].GroupId -and (Test-LayoutBoundsOverlap $bounds[$i] $bounds[$j])) {
                [void]$layoutFailures.Add("元素外框重疊：$($bounds[$i].DisplayName) 與 $($bounds[$j].DisplayName)。")
            }
        }
    }
    foreach ($connection in $connections) {
        foreach ($item in $bounds) {
            if ($connection.EndpointGroups -contains $item.GroupId -or $connection.IgnoredGroups -contains $item.GroupId) { continue }
            if (Test-LineIntersectsBounds $connection $item) {
                [void]$layoutFailures.Add("連線 $($connection.Id) 穿過 $($item.DisplayName)。")
            }
        }
    }
    if ($layoutFailures.Count -gt 0) {
        throw "概念模型「$($Diagram.title)」版面檢查失敗：`n$($layoutFailures -join [Environment]::NewLine)"
    }

    $font = '&quot;Microsoft JhengHei&quot;, &quot;Noto Sans TC&quot;, &quot;PingFang TC&quot;, sans-serif'
    $svg = New-Object System.Collections.Generic.List[string]
    [void]$svg.Add('<?xml version="1.0" encoding="UTF-8"?>')
    [void]$svg.Add("<svg xmlns=`"http://www.w3.org/2000/svg`" width=`"$width`" height=`"$height`" viewBox=`"0 0 $width $height`" role=`"img`" aria-labelledby=`"title description`">")
    [void]$svg.Add("  <title id=`"title`">$(ConvertTo-SvgText $Diagram.title)</title>")
    [void]$svg.Add('  <desc id="description">Chen 記法：矩形為實體、菱形為關聯、橢圓為屬性、底線為主鍵、雙線為全部參與。</desc>')
    [void]$svg.Add("  <rect x=`"0`" y=`"0`" width=`"$width`" height=`"$height`" fill=`"#ffffff`"/>")
    [void]$svg.Add("  <g fill=`"none`" stroke=`"#4a5560`" stroke-width=`"1.5`">")
    foreach ($connection in $connections) {
        [void]$svg.Add(('    <line data-name="{0}" x1="{1:0.##}" y1="{2:0.##}" x2="{3:0.##}" y2="{4:0.##}"/>' -f (ConvertTo-SvgText $connection.Id), $connection.X1, $connection.Y1, $connection.X2, $connection.Y2))
    }
    [void]$svg.Add('  </g>')
    foreach ($entity in $Diagram.entities) {
        $x = [double]$entity.x - ([double]$entity.width / 2); $y = [double]$entity.y - ([double]$entity.height / 2)
        [void]$svg.Add("  <rect data-element=`"entity:$($entity.id)`" x=`"$x`" y=`"$y`" width=`"$($entity.width)`" height=`"$($entity.height)`" fill=`"#eef4f8`" stroke=`"#4a5560`" stroke-width=`"1.5`"/>")
    }
    foreach ($relationship in $Diagram.relationships) {
        $halfWidth = [double]$relationship.width / 2; $halfHeight = [double]$relationship.height / 2
        $points = "$($relationship.x),$([double]$relationship.y - $halfHeight) $([double]$relationship.x + $halfWidth),$($relationship.y) $($relationship.x),$([double]$relationship.y + $halfHeight) $([double]$relationship.x - $halfWidth),$($relationship.y)"
        [void]$svg.Add("  <polygon data-element=`"relationship:$($relationship.id)`" points=`"$points`" fill=`"#fff7df`" stroke=`"#4a5560`" stroke-width=`"1.5`"/>")
    }
    foreach ($entity in $Diagram.entities) {
        foreach ($attribute in $entity.attributes) {
            [void]$svg.Add("  <ellipse data-element=`"attribute:$($entity.id):$($attribute.id)`" cx=`"$($attribute.x)`" cy=`"$($attribute.y)`" rx=`"$($attribute.rx)`" ry=`"$($attribute.ry)`" fill=`"#ffffff`" stroke=`"#4a5560`" stroke-width=`"1.5`"/>")
        }
    }
    foreach ($relationship in $Diagram.relationships) {
        foreach ($attribute in $relationship.attributes) {
            [void]$svg.Add("  <ellipse data-element=`"attribute:$($relationship.id):$($attribute.id)`" cx=`"$($attribute.x)`" cy=`"$($attribute.y)`" rx=`"$($attribute.rx)`" ry=`"$($attribute.ry)`" fill=`"#ffffff`" stroke=`"#4a5560`" stroke-width=`"1.5`"/>")
        }
    }
    [void]$svg.Add("  <g font-family=`"$font`" text-anchor=`"middle`" fill=`"#17212b`">")
    foreach ($label in $labels) {
        $baseline = [double]$label.Shape.Y + ([double]$label.FontSize * 0.35)
        $weight = if ($label.FontSize -eq 15) { '600' } else { '400' }
        [void]$svg.Add("    <text x=`"$($label.Shape.X)`" y=`"$baseline`" font-size=`"$($label.FontSize)`" font-weight=`"$weight`">$(ConvertTo-SvgText $label.Text)</text>")
        if ($label.PrimaryKey) {
            $underlineWidth = [Math]::Min(([double]$label.Shape.Rx * 1.55), ([string]$label.Text).Length * 13)
            $x1 = [double]$label.Shape.X - ($underlineWidth / 2); $x2 = [double]$label.Shape.X + ($underlineWidth / 2)
            $underlineY = $baseline + 3
            [void]$svg.Add(('    <line data-primary-key="{0}" x1="{1:0.##}" y1="{2:0.##}" x2="{3:0.##}" y2="{2:0.##}" stroke="#17212b" stroke-width="1"/>' -f (ConvertTo-SvgText $label.Text), $x1, $underlineY, $x2))
        }
    }
    [void]$svg.Add('  </g>')
    [void]$svg.Add('</svg>')
    return (($svg -join "`n") + "`n")
}

function New-RelationalSchemaSvg {
    param(
        [Parameter(Mandatory = $true)]$DatabaseModel,
        [Parameter(Mandatory = $true)]$Schema
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
        if ($hasAudit) { [void]$items.Add([pscustomobject]@{ Name = '＋ 稽核欄位'; Summary = $true; SummaryType = 'audit' }) }
        if ($hasIdentity) { [void]$items.Add([pscustomobject]@{ Name = '＋ Identity 內建欄位'; Summary = $true; SummaryType = 'identity' }) }
        $displayColumns[$table] = $items
    }

    $svgWidth = 980; $laneSpacing = 14; $leftMargin = 24; $tableNameWidth = 190; $tableGap = 18
    $cellHeight = 34; $baseGap = 12; $endpointSpacing = 9; $interTableGap = 12
    $tableX = $leftMargin + ($sortedMappings.Count * $laneSpacing) + 16
    $fieldX = $tableX + $tableNameWidth + $tableGap
    $fieldAreaWidth = $svgWidth - $fieldX - 22
    $tableLayouts = @{}; $placements = @{}
    foreach ($table in $tableOrder) {
        $lineIndex = 0; $nextX = $fieldX
        $tablePlacements = New-Object System.Collections.Generic.List[object]
        foreach ($column in $displayColumns[$table]) {
            $cellWidth = if ($column.Summary) { [Math]::Max(122, ($column.Name.Length * 8) + 26) } else { [Math]::Max(92, ($column.Name.Length * 8) + 24) }
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
        $mapping | Add-Member -NotePropertyName Lane -NotePropertyValue $i
        $mapping | Add-Member -NotePropertyName SourceGapKey -NotePropertyValue $sourceGapKey
        $mapping | Add-Member -NotePropertyName TargetGapKey -NotePropertyValue $targetGapKey
        $mapping | Add-Member -NotePropertyName SourceSlot -NotePropertyValue $gapNextSlots[$sourceGapKey]
        $mapping | Add-Member -NotePropertyName TargetSlot -NotePropertyValue $gapNextSlots[$targetGapKey]
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

    $legendY = $currentY + 6; $svgHeight = $legendY + 154
    $monoFont = 'Consolas, Menlo, &quot;DejaVu Sans Mono&quot;, monospace'
    $sansFont = '&quot;Microsoft JhengHei&quot;, &quot;Noto Sans TC&quot;, &quot;PingFang TC&quot;, sans-serif'
    $lines = New-Object System.Collections.Generic.List[string]
    [void]$lines.Add('<?xml version="1.0" encoding="UTF-8"?>')
    [void]$lines.Add("<svg xmlns=`"http://www.w3.org/2000/svg`" width=`"$svgWidth`" height=`"$svgHeight`" viewBox=`"0 0 $svgWidth $svgHeight`" role=`"img`" aria-labelledby=`"title description`">")
    [void]$lines.Add("  <title id=`"title`">MEDSUPPLY $([string]$Schema.title)關聯綱目</title>")
    [void]$lines.Add('  <desc id="description">每列代表一張資料表；箭頭由外鍵指向被參照的主鍵；摘要格收起非鍵值的簿記欄位。</desc>')
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
    [void]$lines.Add("  <g class=`"legend`" font-family=`"$sansFont`" fill=`"#263746`">")
    [void]$lines.Add("    <rect x=`"$tableX`" y=`"$legendY`" width=`"$legendWidth`" height=`"138`" rx=`"6`" fill=`"#f7f9fb`" stroke=`"#c6d0da`"/>")
    [void]$lines.Add("    <line x1=`"$($tableX + 18)`" y1=`"$($legendY + 22)`" x2=`"$($tableX + 65)`" y2=`"$($legendY + 22)`" stroke=`"#40566d`" stroke-width=`"2`"/><text x=`"$($tableX + 74)`" y=`"$($legendY + 27)`" font-size=`"12`">CASCADE</text>")
    [void]$lines.Add("    <line x1=`"$($tableX + 155)`" y1=`"$($legendY + 22)`" x2=`"$($tableX + 202)`" y2=`"$($legendY + 22)`" stroke=`"#40566d`" stroke-width=`"2`" stroke-dasharray=`"8 6`"/><text x=`"$($tableX + 211)`" y=`"$($legendY + 27)`" font-size=`"12`">NO ACTION / RESTRICT</text>")
    [void]$lines.Add("    <line x1=`"$($tableX + 390)`" y1=`"$($legendY + 22)`" x2=`"$($tableX + 437)`" y2=`"$($legendY + 22)`" stroke=`"#40566d`" stroke-width=`"2`" stroke-dasharray=`"2 5`"/><text x=`"$($tableX + 446)`" y=`"$($legendY + 27)`" font-size=`"12`">SET NULL</text><text x=`"$($tableX + 560)`" y=`"$($legendY + 27)`" font-size=`"12`" font-weight=`"700`">底線＝主鍵</text>")
    [void]$lines.Add("    <text x=`"$($tableX + 18)`" y=`"$($legendY + 50)`" font-size=`"11`">本 schema 沒有 ON DELETE CASCADE，所以所有箭頭都是虛線。</text>")
    [void]$lines.Add("    <text x=`"$($tableX + 18)`" y=`"$($legendY + 72)`" font-size=`"11`">稽核欄位：IS_DELETED、DELETED_AT、DELETED_BY、CREATED_AT、CREATED_BY、</text>")
    [void]$lines.Add("    <text x=`"$($tableX + 18)`" y=`"$($legendY + 89)`" font-size=`"11`">UPDATED_AT、UPDATED_BY、ROW_VERSION。</text>")
    [void]$lines.Add("    <text x=`"$($tableX + 18)`" y=`"$($legendY + 111)`" font-size=`"11`">Identity 內建欄位：NORMALIZED_*、EMAIL_CONFIRMED、PASSWORD_HASH、SECURITY_STAMP、CONCURRENCY_STAMP、</text>")
    [void]$lines.Add("    <text x=`"$($tableX + 18)`" y=`"$($legendY + 128)`" font-size=`"11`">PHONE_*、TWO_FACTOR_ENABLED、LOCKOUT_*、ACCESS_FAILED_COUNT。</text>")
    [void]$lines.Add('  </g>')
    [void]$lines.Add('</svg>')
    return (($lines -join "`n") + "`n")
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
    $diagram = New-MermaidDiagram -DictionaryRows $dictionaryRows
    $utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
    $expectedArtifacts = [ordered]@{}
    $expectedArtifacts[$OutputPath] = $utf8WithoutBom.GetBytes($diagram)
    foreach ($name in @('requisition', 'identity')) {
        $conceptual = $specification.conceptualDiagrams.$name
        $relational = $specification.relationalSchemas.$name
        $expectedArtifacts[[string]$conceptual.output] = $utf8WithoutBom.GetBytes((New-ConceptualModelSvg -Diagram $conceptual))
        $expectedArtifacts[[string]$relational.output] = $utf8WithoutBom.GetBytes((New-RelationalSchemaSvg -DatabaseModel $databaseModel -Schema $relational))
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
            if (-not (Test-ByteArrayEqual -Left $existingBytes -Right $expectedArtifacts[$path])) {
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
        if (-not (Test-ByteArrayEqual -Left $readmeBytes -Right $expectedReadmeBytes)) {
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
