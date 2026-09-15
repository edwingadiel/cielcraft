<#
.SYNOPSIS
    Rebuilds CielCraft/Data/drops.json, the bundled "which monster drops this
    crafting material" table behind the hunt source (roadmap 7.5).

.DESCRIPTION
    No game sheet maps items to monsters, so the table comes from Garland
    Tools. Three documents are used, all public JSON:

      browse/en/2/mob.json          the mob index: {"i":<id>,"n":name,"l":level,"z":zone}
      mob/en/2/<id>.json            one mob: {"name","zoneid","lvl","drops":[itemId...]}
      item/en/3/<id>.json           one item; "ingredient_of" says it is a craft material
      core/en/3/data.json           locationIndex: Garland zone id -> place name

    Garland's mob ids carry the game's BNpcName row in their low ten digits
    (verified 2026-09-15 against the BNpcName sheet: 20000000002 -> row 2
    "ruins runner", 8270000000006 -> row 6 "lemur", 51160000000013 -> row 13
    "arbor buzzard"), which is what the plugin needs to recognise a mob in the
    object table. Garland's zone ids are the game's PlaceName row ids
    (verified: 42 "Western Thanalan", 56 "South Shroud", 404 "Snowcloak"), but
    the file stores the place *name* and lets the plugin resolve it against
    TerritoryType at run time, so a patch that renumbers nothing but rows
    cannot rot the bundle.

    Be polite: every request is spaced by -DelayMs and every document is
    cached under %LOCALAPPDATA%\CielCraft\garland, so a re-run after an
    interruption downloads only what is missing. A full cold run is roughly
    5 100 mob documents plus ~2 500 item documents.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/refresh-drops.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/refresh-drops.ps1 -MaxMobs 50 -Output out.json
#>
[CmdletBinding()]
param(
    # Pause between two web requests. Garland Tools is a hobby site; do not lower this much.
    [int] $DelayMs = 300,

    # Resumable download cache; delete it (or pass -Refresh) to start over.
    [string] $CacheDir = (Join-Path $env:LOCALAPPDATA 'CielCraft\garland'),

    # Where the bundled table is written; empty = CielCraft\Data\drops.json beside this script's repository.
    [string] $Output = '',

    # Only look at the first N mobs (smoke-testing the script).
    [int] $MaxMobs = 0,

    # Keep every dropped item instead of only the ones a recipe uses.
    [switch] $NoIngredientFilter,

    # Ignore the cache and download everything again.
    [switch] $Refresh
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$script:UserAgent = 'CielCraft-refresh-drops/1.0 (+https://github.com/edwingadiel/cielcraft)'
$script:Downloads = 0

if ([string]::IsNullOrWhiteSpace($Output)) {
    # $PSScriptRoot is empty when the script is piped into powershell.exe
    # rather than run with -File; fall back to the invocation path, then cwd.
    $scriptDir = $PSScriptRoot
    if ([string]::IsNullOrWhiteSpace($scriptDir) -and $MyInvocation.MyCommand.Path) {
        $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    }

    $Output = if ([string]::IsNullOrWhiteSpace($scriptDir)) {
        Join-Path (Get-Location).Path 'CielCraft\Data\drops.json'
    }
    else {
        Join-Path $scriptDir '..\CielCraft\Data\drops.json'
    }
}

function Get-CachedJson {
    <#
      One cached GET. Returns the parsed document, or $null when Garland has
      no such document (a 404 is cached as an empty file so a re-run does not
      ask again).
    #>
    param(
        [Parameter(Mandatory = $true)][string] $Url,
        [Parameter(Mandatory = $true)][string] $CachePath
    )

    $dir = Split-Path -Parent $CachePath
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

    if ((-not $Refresh) -and (Test-Path $CachePath)) {
        $cached = Get-Content -LiteralPath $CachePath -Raw -Encoding UTF8
        if ([string]::IsNullOrWhiteSpace($cached)) { return $null }
        try { return $cached | ConvertFrom-Json } catch { Remove-Item -LiteralPath $CachePath -Force }
    }

    # Throttle before the request, never after the last one.
    if ($script:Downloads -gt 0 -and $DelayMs -gt 0) { Start-Sleep -Milliseconds $DelayMs }
    $script:Downloads++

    try {
        $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 60 -UserAgent $script:UserAgent
    }
    catch {
        $status = $null
        if ($_.Exception.Response) { $status = [int] $_.Exception.Response.StatusCode }
        if ($status -eq 404) {
            # Remember the hole, do not ask again.
            Set-Content -LiteralPath $CachePath -Value '' -Encoding UTF8
            return $null
        }

        Write-Warning "GET $Url failed ($($_.Exception.Message)); retrying once in 5s."
        Start-Sleep -Seconds 5
        $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 60 -UserAgent $script:UserAgent
    }

    Set-Content -LiteralPath $CachePath -Value $response.Content -Encoding UTF8
    return $response.Content | ConvertFrom-Json
}

function ConvertTo-MinLevel {
    # Garland levels are display strings: "50", "20 - 23", "??".
    param([string] $Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return 0 }
    $match = [regex]::Match($Text, '\d+')
    if (-not $match.Success) { return 0 }
    return [int] $match.Value
}

function ConvertTo-JsonString {
    param([string] $Value)
    if ($null -eq $Value) { return '""' }
    $escaped = $Value -replace '\\', '\\\\' -replace '"', '\"' -replace "`r", '\r' -replace "`n", '\n' -replace "`t", '\t'
    return '"' + $escaped + '"'
}

# --------------------------------------------------------------- zone index

Write-Host "Cache: $CacheDir"
$core = Get-CachedJson -Url 'https://garlandtools.org/db/doc/core/en/3/data.json' -CachePath (Join-Path $CacheDir 'core-data.json')
$zoneNames = @{}
if ($core -and $core.locationIndex) {
    foreach ($property in $core.locationIndex.PSObject.Properties) {
        $zoneNames[[string] $property.Name] = [string] $property.Value.name
    }
}
Write-Host "Zone index: $($zoneNames.Count) places."

# ----------------------------------------------------------------- mob list

$index = Get-CachedJson -Url 'https://garlandtools.org/db/doc/browse/en/2/mob.json' -CachePath (Join-Path $CacheDir 'mob-index.json')
if (-not $index -or -not $index.browse) { throw 'The Garland mob index came back empty.' }

$mobs = @($index.browse)
if ($MaxMobs -gt 0 -and $mobs.Count -gt $MaxMobs) { $mobs = $mobs[0..($MaxMobs - 1)] }
Write-Host "Mob index: $($mobs.Count) entries."

# item id -> list of mob entries; deduplicated on (BNpcName row, zone), since
# Garland lists the same monster once per spawn group.
$byItem = @{}
$done = 0

foreach ($entry in $mobs) {
    $done++
    if ($done % 250 -eq 0) { Write-Host "  mobs $done/$($mobs.Count) ($script:Downloads downloads)" }

    $garlandId = [int64] $entry.i
    # The low ten digits are the BNpcName row; the high digits are Garland's
    # own per-spawn counter (several rows share one BNpcName across zones).
    $bnpc = [int64] ($garlandId % 10000000000)
    if ($bnpc -le 0) { continue }

    $doc = Get-CachedJson -Url "https://garlandtools.org/db/doc/mob/en/2/$garlandId.json" -CachePath (Join-Path $CacheDir "mob\$garlandId.json")
    $mob = if ($doc) { $doc.mob } else { $null }
    if (-not $mob -or -not $mob.drops) { continue }

    $zoneId = if ($mob.zoneid) { [string] $mob.zoneid } else { [string] $entry.z }
    $zone = if ($zoneId -and $zoneNames.ContainsKey($zoneId)) { $zoneNames[$zoneId] } else { '' }
    if ([string]::IsNullOrWhiteSpace($zone)) { continue }

    $name = if ($mob.name) { [string] $mob.name } else { [string] $entry.n }
    $levelText = if ($mob.lvl) { [string] $mob.lvl } else { [string] $entry.l }
    $level = ConvertTo-MinLevel $levelText

    foreach ($drop in $mob.drops) {
        $itemId = [int] $drop
        if ($itemId -le 0) { continue }
        if (-not $byItem.ContainsKey($itemId)) { $byItem[$itemId] = New-Object 'System.Collections.Generic.List[object]' }
        $list = $byItem[$itemId]
        $duplicate = $false
        foreach ($known in $list) { if ($known.bnpc -eq $bnpc -and $known.zone -eq $zone) { $duplicate = $true; break } }
        if ($duplicate) { continue }
        $list.Add([pscustomobject]@{ bnpc = $bnpc; name = $name; level = $level; zone = $zone })
    }
}

Write-Host "Dropped items: $($byItem.Count)."

# ------------------------------------------------- keep the craft materials

$kept = @{}
if ($NoIngredientFilter) {
    $kept = $byItem
    Write-Host 'Ingredient filter off: every dropped item is kept.'
}
else {
    $itemIds = @($byItem.Keys | Sort-Object)
    $done = 0
    foreach ($itemId in $itemIds) {
        $done++
        if ($done % 250 -eq 0) { Write-Host "  items $done/$($itemIds.Count) ($script:Downloads downloads)" }

        $doc = Get-CachedJson -Url "https://garlandtools.org/db/doc/item/en/3/$itemId.json" -CachePath (Join-Path $CacheDir "item\$itemId.json")
        if (-not $doc -or -not $doc.item) { continue }
        $usedBy = $doc.item.ingredient_of
        if (-not $usedBy) { continue }
        if (@($usedBy.PSObject.Properties).Count -eq 0) { continue }
        $kept[$itemId] = $byItem[$itemId]
    }

    Write-Host "Craft materials among them: $($kept.Count)."
}

# ------------------------------------------------------------------- output

$builder = New-Object System.Text.StringBuilder
[void] $builder.Append("[`r`n")
$first = $true
foreach ($itemId in ($kept.Keys | Sort-Object)) {
    if (-not $first) { [void] $builder.Append(",`r`n") }
    $first = $false

    # Lowest level first: the hunt run prefers the mob it can kill safely.
    $entries = @($kept[$itemId] | Sort-Object -Property level, zone, name)
    [void] $builder.Append('  { "item": ').Append($itemId).Append(', "mobs": [')
    for ($i = 0; $i -lt $entries.Count; $i++) {
        if ($i -gt 0) { [void] $builder.Append(', ') }
        $mob = $entries[$i]
        [void] $builder.Append('{ "bnpc": ').Append($mob.bnpc)
        [void] $builder.Append(', "name": ').Append((ConvertTo-JsonString $mob.name))
        [void] $builder.Append(', "level": ').Append($mob.level)
        [void] $builder.Append(', "zone": ').Append((ConvertTo-JsonString $mob.zone))
        [void] $builder.Append(' }')
    }
    [void] $builder.Append('] }')
}
[void] $builder.Append("`r`n]`r`n")

$outputPath = [IO.Path]::GetFullPath($Output)
$outputDir = Split-Path -Parent $outputPath
if (-not (Test-Path $outputDir)) { New-Item -ItemType Directory -Path $outputDir -Force | Out-Null }
[IO.File]::WriteAllText($outputPath, $builder.ToString(), (New-Object Text.UTF8Encoding($false)))

$size = (Get-Item -LiteralPath $outputPath).Length
Write-Host "Wrote $outputPath - $($kept.Count) items, $([Math]::Round($size / 1KB)) KB, $script:Downloads downloads this run."
