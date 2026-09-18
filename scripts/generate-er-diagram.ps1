<#
.SYNOPSIS
    從 Oracle 資料字典產生 MEDSUPPLY 的 Mermaid ER 圖與關聯綱目 SVG，或檢查提交的圖是否漂移。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts/generate-er-diagram.ps1
    powershell -ExecutionPolicy Bypass -File scripts/generate-er-diagram.ps1 -Check
#>

[CmdletBinding()]
param(
    [switch]$Check,
    [string]$ContainerName = 'medsupplyops-oracle',
    [string]$OutputPath = 'docs/diagrams/schema.mmd',
    [string]$RelationalSchemaPath = 'docs/diagrams/relational-schema.svg',
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
    $lines.Add('<!-- 本檔由 scripts/generate-er-diagram.ps1 產生，請勿手動編輯。產生指令：powershell -ExecutionPolicy Bypass -File scripts/generate-er-diagram.ps1 -->')
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

function New-RelationalSchemaSvg {
    param([Parameter(Mandatory = $true)][string[]]$DictionaryRows)

    $tables = New-Object System.Collections.Generic.List[string]
    $columns = @{}
    $primaryKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
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

    if ($tables.Count -eq 0 -or $mappings.Count -eq 0) {
        throw '資料字典缺少資料表或外鍵欄位對應，無法產生關聯綱目 SVG。'
    }

    $sortedMappings = @($mappings | Sort-Object Constraint, Position)
    foreach ($mapping in $sortedMappings) {
        if ([string]::IsNullOrWhiteSpace($mapping.DeleteRule)) {
            throw "資料字典查不到外鍵 $($mapping.Constraint) 的 DELETE_RULE。"
        }
        if (-not $primaryKeys.Contains("$($mapping.ParentTable)|$($mapping.ParentColumn)")) {
            throw "外鍵 $($mapping.Constraint) 的目標 $($mapping.ParentTable).$($mapping.ParentColumn) 不是主鍵欄位。"
        }
    }

    $tableOrder = Get-TopologicalTableOrder -Tables $tables.ToArray() -Mappings $sortedMappings
    $tableOrderIndex = @{}
    for ($i = 0; $i -lt $tableOrder.Count; $i++) { $tableOrderIndex[$tableOrder[$i]] = $i }
    $sortedMappings = @($sortedMappings | Sort-Object @{ Expression = { $tableOrderIndex[$_.ChildTable] } }, Constraint, Position)

    $laneSpacing = 18
    $leftMargin = 26
    $tableNameWidth = 230
    $fieldAreaWidth = 1100
    $cellHeight = 36
    $baseGap = 14
    $endpointSpacing = 10
    $interTableGap = 14
    $tableX = $leftMargin + ($sortedMappings.Count * $laneSpacing) + 18
    $fieldX = $tableX + $tableNameWidth
    $svgWidth = $fieldX + $fieldAreaWidth + 30

    $tableLayouts = @{}
    $placements = @{}
    foreach ($table in $tableOrder) {
        $lineIndex = 0
        $nextX = $fieldX
        $tablePlacements = New-Object System.Collections.Generic.List[object]
        foreach ($column in $columns[$table]) {
            $cellWidth = [Math]::Max(104, ($column.Length * 9) + 28)
            if ($nextX -gt $fieldX -and ($nextX + $cellWidth) -gt ($fieldX + $fieldAreaWidth)) {
                $lineIndex++
                $nextX = $fieldX
            }
            $placement = [pscustomobject]@{
                Table = $table
                Column = $column
                X = $nextX
                Y = 0
                Width = $cellWidth
                Line = $lineIndex
            }
            [void]$tablePlacements.Add($placement)
            $placements["$table|$column"] = $placement
            $nextX += $cellWidth
        }
        $tableLayouts[$table] = [pscustomobject]@{
            Placements = $tablePlacements
            LineCount = $lineIndex + 1
            StartY = 0
        }
    }

    $gapEndpointCounts = @{}
    $gapNextSlots = @{}
    for ($i = 0; $i -lt $sortedMappings.Count; $i++) {
        $mapping = $sortedMappings[$i]
        $sourcePlacement = $placements["$($mapping.ChildTable)|$($mapping.ChildColumn)"]
        $targetPlacement = $placements["$($mapping.ParentTable)|$($mapping.ParentColumn)"]
        if ($null -eq $sourcePlacement -or $null -eq $targetPlacement) {
            throw "外鍵 $($mapping.Constraint) 的欄位對應不存在：$($mapping.ChildTable).$($mapping.ChildColumn) → $($mapping.ParentTable).$($mapping.ParentColumn)。"
        }

        $sourceGapKey = "$($mapping.ChildTable)|$($sourcePlacement.Line)"
        $targetGapKey = "$($mapping.ParentTable)|$($targetPlacement.Line)"
        foreach ($gapKey in @($sourceGapKey, $targetGapKey)) {
            if (-not $gapEndpointCounts.ContainsKey($gapKey)) { $gapEndpointCounts[$gapKey] = 0 }
            $gapEndpointCounts[$gapKey]++
        }
        if (-not $gapNextSlots.ContainsKey($sourceGapKey)) { $gapNextSlots[$sourceGapKey] = 0 }
        if (-not $gapNextSlots.ContainsKey($targetGapKey)) { $gapNextSlots[$targetGapKey] = 0 }
        $sourceSlot = $gapNextSlots[$sourceGapKey]
        $targetSlot = $gapNextSlots[$targetGapKey]
        $gapNextSlots[$sourceGapKey]++
        $gapNextSlots[$targetGapKey]++

        $mapping | Add-Member -NotePropertyName Lane -NotePropertyValue $i
        $mapping | Add-Member -NotePropertyName SourceGapKey -NotePropertyValue $sourceGapKey
        $mapping | Add-Member -NotePropertyName TargetGapKey -NotePropertyValue $targetGapKey
        $mapping | Add-Member -NotePropertyName SourceSlot -NotePropertyValue $sourceSlot
        $mapping | Add-Member -NotePropertyName TargetSlot -NotePropertyValue $targetSlot
    }

    $gapBottoms = @{}
    $currentY = 28
    foreach ($table in $tableOrder) {
        $layout = $tableLayouts[$table]
        $layout.StartY = $currentY
        for ($line = 0; $line -lt $layout.LineCount; $line++) {
            foreach ($placement in $layout.Placements | Where-Object { $_.Line -eq $line }) {
                $placement.Y = $currentY
            }
            $gapKey = "$table|$line"
            $gapBottoms[$gapKey] = $currentY + $cellHeight
            $endpointCount = if ($gapEndpointCounts.ContainsKey($gapKey)) { $gapEndpointCounts[$gapKey] } else { 0 }
            $currentY += $cellHeight + $baseGap + ($endpointCount * $endpointSpacing)
        }
        $currentY += $interTableGap
    }

    $legendY = $currentY + 8
    $svgHeight = $legendY + 92
    $monoFont = 'Consolas, Menlo, &quot;DejaVu Sans Mono&quot;, monospace'
    $legendFont = '&quot;Segoe UI&quot;, &quot;Noto Sans TC&quot;, &quot;Microsoft JhengHei&quot;, sans-serif'
    $lines = New-Object System.Collections.Generic.List[string]
    [void]$lines.Add('<?xml version="1.0" encoding="UTF-8"?>')
    [void]$lines.Add("<svg xmlns=`"http://www.w3.org/2000/svg`" width=`"$svgWidth`" height=`"$svgHeight`" viewBox=`"0 0 $svgWidth $svgHeight`" role=`"img`" aria-labelledby=`"title description`">")
    [void]$lines.Add('  <title id="title">MEDSUPPLY 關聯綱目</title>')
    [void]$lines.Add('  <desc id="description">每列代表一張資料表；欄位格之間的箭頭由外鍵指向被參照的主鍵。</desc>')
    [void]$lines.Add('  <defs>')
    [void]$lines.Add('    <marker id="arrow" markerWidth="10" markerHeight="10" refX="9" refY="5" orient="auto" markerUnits="strokeWidth"><path d="M 0 0 L 10 5 L 0 10 z" fill="#40566d"/></marker>')
    [void]$lines.Add('  </defs>')
    [void]$lines.Add("  <rect x=`"0`" y=`"0`" width=`"$svgWidth`" height=`"$svgHeight`" fill=`"#ffffff`"/>")

    foreach ($table in $tableOrder) {
        $layout = $tableLayouts[$table]
        [void]$lines.Add("  <g class=`"table`" data-table=`"$table`">")
        [void]$lines.Add("    <rect x=`"$tableX`" y=`"$($layout.StartY)`" width=`"$tableNameWidth`" height=`"$cellHeight`" fill=`"#e7eef6`" stroke=`"#6f8295`"/>")
        $tableTextX = $tableX + [int]($tableNameWidth / 2)
        $tableTextY = $layout.StartY + 23
        [void]$lines.Add("    <text x=`"$tableTextX`" y=`"$tableTextY`" text-anchor=`"middle`" font-family=`"$monoFont`" font-size=`"14`" font-weight=`"700`" fill=`"#162536`">$(ConvertTo-SvgText $table)</text>")
        foreach ($placement in $layout.Placements) {
            $columnKey = "$table|$($placement.Column)"
            $isPrimaryKey = $primaryKeys.Contains($columnKey)
            [void]$lines.Add("    <rect x=`"$($placement.X)`" y=`"$($placement.Y)`" width=`"$($placement.Width)`" height=`"$cellHeight`" fill=`"#ffffff`" stroke=`"#8c9aa8`"/>")
            $textX = $placement.X + [int]($placement.Width / 2)
            $textY = $placement.Y + 23
            $weight = if ($isPrimaryKey) { '700' } else { '400' }
            [void]$lines.Add("    <text x=`"$textX`" y=`"$textY`" text-anchor=`"middle`" font-family=`"$monoFont`" font-size=`"13`" font-weight=`"$weight`" fill=`"#162536`">$(ConvertTo-SvgText $placement.Column)</text>")
            if ($isPrimaryKey) {
                $underlineWidth = [Math]::Min($placement.Width - 20, $placement.Column.Length * 8)
                $underlineX1 = $textX - [int]($underlineWidth / 2)
                $underlineX2 = $underlineX1 + $underlineWidth
                $underlineY = $placement.Y + 27
                [void]$lines.Add("    <line x1=`"$underlineX1`" y1=`"$underlineY`" x2=`"$underlineX2`" y2=`"$underlineY`" stroke=`"#162536`" stroke-width=`"1`"/>")
            }
        }
        [void]$lines.Add('  </g>')
    }

    foreach ($mapping in $sortedMappings) {
        $source = $placements["$($mapping.ChildTable)|$($mapping.ChildColumn)"]
        $target = $placements["$($mapping.ParentTable)|$($mapping.ParentColumn)"]
        $sourceX = $source.X + [int]($source.Width / 2)
        $sourceBottom = $source.Y + $cellHeight
        $targetX = $target.X + [int]($target.Width / 2)
        $targetBottom = $target.Y + $cellHeight
        $sourceY = $gapBottoms[$mapping.SourceGapKey] + 8 + ($mapping.SourceSlot * $endpointSpacing)
        $targetY = $gapBottoms[$mapping.TargetGapKey] + 8 + ($mapping.TargetSlot * $endpointSpacing)
        $laneX = $leftMargin + ($mapping.Lane * $laneSpacing)
        switch ($mapping.DeleteRule) {
            'CASCADE' { $dashAttribute = '' }
            'NO ACTION' { $dashAttribute = ' stroke-dasharray="8 6"' }
            'SET NULL' { $dashAttribute = ' stroke-dasharray="2 5"' }
            default { throw "外鍵 $($mapping.Constraint) 使用未支援的 DELETE_RULE：$($mapping.DeleteRule)。" }
        }
        $sourceLabel = "$($mapping.ChildTable).$($mapping.ChildColumn)"
        $targetLabel = "$($mapping.ParentTable).$($mapping.ParentColumn)"
        [void]$lines.Add("  <g class=`"foreign-key`" data-constraint=`"$(ConvertTo-SvgText $mapping.Constraint)`" data-source=`"$sourceLabel`" data-target=`"$targetLabel`" data-delete-rule=`"$($mapping.DeleteRule)`">")
        [void]$lines.Add("    <title>$(ConvertTo-SvgText "$sourceLabel → $targetLabel ($($mapping.DeleteRule))")</title>")
        [void]$lines.Add("    <path d=`"M $sourceX $sourceBottom V $sourceY H $laneX V $targetY H $targetX V $targetBottom`" fill=`"none`" stroke=`"#40566d`" stroke-width=`"1.5`"$dashAttribute marker-end=`"url(#arrow)`"/>")
        [void]$lines.Add('  </g>')
    }

    [void]$lines.Add("  <g class=`"legend`" font-family=`"$legendFont`" font-size=`"13`" fill=`"#263746`">")
    [void]$lines.Add("    <rect x=`"$tableX`" y=`"$legendY`" width=`"$($svgWidth - $tableX - 30)`" height=`"70`" rx=`"6`" fill=`"#f7f9fb`" stroke=`"#c6d0da`"/>")
    $legendLineY = $legendY + 24
    [void]$lines.Add("    <line x1=`"$($tableX + 20)`" y1=`"$legendLineY`" x2=`"$($tableX + 75)`" y2=`"$legendLineY`" stroke=`"#40566d`" stroke-width=`"2`"/>")
    [void]$lines.Add("    <text x=`"$($tableX + 84)`" y=`"$($legendLineY + 5)`">CASCADE</text>")
    [void]$lines.Add("    <line x1=`"$($tableX + 190)`" y1=`"$legendLineY`" x2=`"$($tableX + 245)`" y2=`"$legendLineY`" stroke=`"#40566d`" stroke-width=`"2`" stroke-dasharray=`"8 6`"/>")
    [void]$lines.Add("    <text x=`"$($tableX + 254)`" y=`"$($legendLineY + 5)`">NO ACTION / RESTRICT</text>")
    [void]$lines.Add("    <line x1=`"$($tableX + 455)`" y1=`"$legendLineY`" x2=`"$($tableX + 510)`" y2=`"$legendLineY`" stroke=`"#40566d`" stroke-width=`"2`" stroke-dasharray=`"2 5`"/>")
    [void]$lines.Add("    <text x=`"$($tableX + 519)`" y=`"$($legendLineY + 5)`">SET NULL</text>")
    [void]$lines.Add("    <text x=`"$($tableX + 650)`" y=`"$($legendLineY + 5)`" font-weight=`"700`">底線＝主鍵</text>")
    [void]$lines.Add("    <text x=`"$($tableX + 20)`" y=`"$($legendLineY + 32)`">本 schema 沒有 ON DELETE CASCADE，所以所有箭頭都是虛線。</text>")
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
    $diagram = New-MermaidDiagram -DictionaryRows $dictionaryRows
    $relationalSchemaSvg = New-RelationalSchemaSvg -DictionaryRows $dictionaryRows
    $utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
    $expectedBytes = $utf8WithoutBom.GetBytes($diagram)
    $expectedRelationalSchemaBytes = $utf8WithoutBom.GetBytes($relationalSchemaSvg)
    $absoluteOutputPath = Join-Path $repoRoot $OutputPath
    $absoluteRelationalSchemaPath = Join-Path $repoRoot $RelationalSchemaPath

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
        if (-not (Test-Path -LiteralPath $absoluteOutputPath)) {
            Write-Host "ER 圖漂移：找不到 $OutputPath。請先執行產生模式。" -ForegroundColor Red
            exit 1
        }

        $existingBytes = [System.IO.File]::ReadAllBytes($absoluteOutputPath)
        if (-not (Test-ByteArrayEqual -Left $existingBytes -Right $expectedBytes)) {
            $existingText = [System.Text.Encoding]::UTF8.GetString($existingBytes)
            Write-DriftDiff -ExistingText $existingText -ExpectedText $diagram
            exit 1
        }

        if (-not (Test-Path -LiteralPath $absoluteRelationalSchemaPath)) {
            Write-Host "關聯綱目 SVG 漂移：找不到 $RelationalSchemaPath。請先執行產生模式。" -ForegroundColor Red
            exit 1
        }

        $existingRelationalSchemaBytes = [System.IO.File]::ReadAllBytes($absoluteRelationalSchemaPath)
        if (-not (Test-ByteArrayEqual -Left $existingRelationalSchemaBytes -Right $expectedRelationalSchemaBytes)) {
            Write-Host "關聯綱目 SVG 漂移：資料字典產生的內容與 $RelationalSchemaPath 不一致。" -ForegroundColor Red
            Write-Host '請執行產生模式（不加 -Check）重新同步。' -ForegroundColor Yellow
            exit 1
        }

        # ★ 第二份：README 內嵌的實體關係圖。schema.mmd 對了不代表 README 也對——
        #   README 是最多人看的那一份，它過期比 .mmd 過期更糟。
        if (-not (Test-ByteArrayEqual -Left $readmeBytes -Right $expectedReadmeBytes)) {
            Write-Host "ER 圖漂移：$ReadmePath 內嵌的實體關係圖與資料字典不一致。" -ForegroundColor Red
            Write-Host "請執行產生模式（不加 -Check）重新同步。" -ForegroundColor Yellow
            exit 1
        }

        Write-Host "ER 圖與關聯綱目 SVG 均和資料字典一致：$OutputPath、$RelationalSchemaPath、$ReadmePath" -ForegroundColor Green
        exit 0
    }

    $outputDirectory = Split-Path -Parent $absoluteOutputPath
    if (-not (Test-Path -LiteralPath $outputDirectory)) {
        New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    }
    [System.IO.File]::WriteAllBytes($absoluteOutputPath, $expectedBytes)
    [System.IO.File]::WriteAllBytes($absoluteRelationalSchemaPath, $expectedRelationalSchemaBytes)
    [System.IO.File]::WriteAllBytes($absoluteReadmePath, $expectedReadmeBytes)
    Write-Host "已從 Oracle 資料字典產生 $OutputPath、$RelationalSchemaPath 與 $ReadmePath 的實體關係圖／關聯綱目" -ForegroundColor Green
}
catch {
    Write-Host "產生資料模型圖失敗：$($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
