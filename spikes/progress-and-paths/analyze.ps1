# Summarise a run captured by run.ps1: line counts per stream and kind, non-prefixed lines, JSON validity,
# whether the bytes are pure ASCII / valid UTF-8, and the distinct post-processors seen.
[CmdletBinding()]
param([Parameter(Mandatory)] [string]$Case)

$ErrorActionPreference = 'Stop'
$dir = Join-Path $PSScriptRoot "out\$Case"
$prefix = 'YDH1 '
$strict = [Text.UTF8Encoding]::new($false, $true)

foreach ($stream in 'stdout', 'stderr') {
    $bytes = [IO.File]::ReadAllBytes((Join-Path $dir "$stream.bin"))
    $ascii = -not ($bytes | Where-Object { $_ -gt 0x7F } | Select-Object -First 1)
    try { $text = $strict.GetString($bytes); $utf8 = $true } catch { $text = [Text.Encoding]::Latin1.GetString($bytes); $utf8 = $false }
    $lines = $text -split "`r?`n" | Where-Object { $_ -ne '' }
    $crlf = $text.Contains("`r`n")
    Write-Host "== $stream : $($bytes.Length) B, $($lines.Count) lines, pureAscii=$ascii validUtf8=$utf8 crlf=$crlf"
    $kinds = @{}; $bad = 0; $pps = [ordered]@{}; $statuses = [ordered]@{}
    foreach ($l in $lines) {
        if ($l.StartsWith($prefix, [StringComparison]::Ordinal)) {
            try { $o = $l.Substring($prefix.Length) | ConvertFrom-Json } catch { $bad++; continue }
            $kinds[$o.k] = 1 + [int]$kinds[$o.k]
            if ($o.k -eq 'pp') { $pps["$($o.p.postprocessor):$($o.p.status)"] = 1 }
            if ($o.k -eq 'dl') { $statuses["$($o.i.format_id):$($o.p.status)"] = 1 + [int]$statuses["$($o.i.format_id):$($o.p.status)"] }
        } else {
            Write-Host "   other: $l"
        }
    }
    $kinds.GetEnumerator() | ForEach-Object { Write-Host "   kind $($_.Key): $($_.Value)" }
    if ($bad) { Write-Host "   INVALID JSON lines: $bad" }
    if ($statuses.Count) { Write-Host "   dl format:status counts: $(($statuses.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', ')" }
    if ($pps.Count) { Write-Host "   pp sequence: $($pps.Keys -join ' > ')" }
}
Write-Host "exit: $(Get-Content (Join-Path $dir 'exit.txt'))"
