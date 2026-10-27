# Throwaway spike for issue #35: check which saved jars still unlock a members-only video, over hours/days.
# Each jar is tested on a throwaway copy (yt-dlp writes cookies back on exit), so the saved jars never change.
# Appends one row per jar to logs\lifetime.csv. Rerun now and then, e.g. a few times a day.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$MembersUrl,
    [string[]]$Jars = @(Get-ChildItem (Join-Path $PSScriptRoot 'out') -Filter 'cookies-gen*.txt' | Where-Object Name -Match '^cookies-gen\d+\.txt$' | Sort-Object { [int]($_.BaseName -replace '\D') } | ForEach-Object FullName),
    [string]$YtDlp = $(if (Get-Command yt-dlp -ErrorAction SilentlyContinue) { (Get-Command yt-dlp).Source } else { 'C:\tools\yt-dlp.exe' })
)

$ErrorActionPreference = 'Stop'
$env:PYTHONUTF8 = '1'

$logDir = Join-Path $PSScriptRoot 'logs'
$csv = Join-Path $logDir 'lifetime.csv'
New-Item -ItemType Directory -Force $logDir | Out-Null
$tmp = Join-Path ([IO.Path]::GetTempPath()) "ydh-lifetime-$PID.txt"

$rows = foreach ($jar in $Jars) {
    Copy-Item -LiteralPath $jar $tmp -Force
    $out = & $YtDlp --ignore-config --js-runtimes deno --cookies $tmp --skip-download --print id $MembersUrl 2>&1 | Out-String
    $ok = $LASTEXITCODE -eq 0
    Remove-Item $tmp -Force
    [pscustomobject]@{
        CheckedUtc = [DateTime]::UtcNow.ToString('yyyy-MM-dd HH:mm', [Globalization.CultureInfo]::InvariantCulture)
        Jar        = Split-Path $jar -Leaf
        JarAgeH    = [Math]::Round(([DateTime]::Now - (Get-Item -LiteralPath $jar).LastWriteTime).TotalHours, 1)
        MembersOk  = $ok
        Error      = if ($ok) { '' } else { ([regex]::Match($out, '(?m)^ERROR: .*$')).Value }
    }
}

$rows | Export-Csv -LiteralPath $csv -Append -NoTypeInformation -Encoding utf8
$rows | Format-Table -AutoSize
Write-Host "Appended to $csv"
