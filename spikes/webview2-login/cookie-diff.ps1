# Throwaway spike for issue #35: compare two Netscape cookie files without printing cookie VALUES.
# Each cookie is keyed by domain + path + name. Output: added / removed / changed (value, expiry, flags) / same.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Before,
    [Parameter(Mandatory)][string]$After,
    # Only show cookies under these domains (empty = all).
    [string[]]$Domain = @(),
    # Also list unchanged cookies.
    [switch]$All
)

$ErrorActionPreference = 'Stop'

function Read-Jar([string]$path) {
    $jar = [ordered]@{}
    foreach ($line in Get-Content -LiteralPath $path -Encoding UTF8) {
        $httpOnly = $false
        if ($line.StartsWith('#HttpOnly_', [StringComparison]::Ordinal)) {
            $httpOnly = $true
            $line = $line.Substring(10)
        } elseif ($line.StartsWith('#', [StringComparison]::Ordinal) -or -not $line.Trim()) {
            continue
        }

        $f = $line.Split("`t")
        if ($f.Count -lt 7) { continue }
        $c = [pscustomobject]@{
            Domain   = $f[0]
            Sub      = $f[1]
            Path     = $f[2]
            Secure   = $f[3]
            Expires  = [long]$f[4]
            Name     = $f[5]
            Value    = ($f[6..($f.Count - 1)] -join "`t")
            HttpOnly = $httpOnly
        }
        $jar["$($c.Domain)`t$($c.Path)`t$($c.Name)"] = $c
    }
    return $jar
}

function Format-Expiry([long]$unix) {
    if ($unix -eq 0) { return 'session' }
    return [DateTimeOffset]::FromUnixTimeSeconds($unix).UtcDateTime.ToString('yyyy-MM-dd HH:mm', [Globalization.CultureInfo]::InvariantCulture)
}

function Test-Domain([string]$cookieDomain) {
    if (-not $Domain) { return $true }
    $d = $cookieDomain.TrimStart('.')
    foreach ($root in $Domain) {
        if ($d -eq $root -or $d.EndsWith(".$root", [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

$a = Read-Jar $Before
$b = Read-Jar $After
$keys = @($a.Keys) + @($b.Keys) | Sort-Object -Unique

$rows = foreach ($k in $keys) {
    $x = $a[$k]; $y = $b[$k]
    $c = if ($y) { $y } else { $x }
    if (-not (Test-Domain $c.Domain)) { continue }

    $status = if (-not $x) { 'added' } elseif (-not $y) { 'REMOVED' } else {
        $changes = @()
        if ($x.Value -ne $y.Value) {
            $changes += if (-not $y.Value) { 'VALUE BLANKED' } else { 'value' }
        }
        if ($x.Expires -ne $y.Expires) { $changes += 'expiry' }
        if ($x.HttpOnly -ne $y.HttpOnly) { $changes += 'httpOnly' }
        if ($x.Secure -ne $y.Secure -or $x.Sub -ne $y.Sub) { $changes += 'flags' }
        if ($changes) { $changes -join '+' } else { 'same' }
    }
    if ($status -eq 'same' -and -not $All) { continue }

    [pscustomobject]@{
        Status        = $status
        Domain        = $c.Domain
        Name          = $c.Name
        ExpiresBefore = if ($x) { Format-Expiry $x.Expires } else { '-' }
        ExpiresAfter  = if ($y) { Format-Expiry $y.Expires } else { '-' }
        LenBefore     = if ($x) { $x.Value.Length } else { '-' }
        LenAfter      = if ($y) { $y.Value.Length } else { '-' }
    }
}

Write-Host "Before: $Before ($($a.Count) cookies)"
Write-Host "After:  $After ($($b.Count) cookies)"
if ($rows) { $rows | Format-Table -AutoSize | Out-String -Width 200 | Write-Host } else { Write-Host 'No differences.' }
