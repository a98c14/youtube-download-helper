# Throwaway spike for issue #31: run yt-dlp with the cookies exported by the harness.
# Each case lists formats (-F) and then downloads, with verbose logs in logs\.
[CmdletBinding()]
param(
    [string]$MembersUrl,
    [string]$PremiumUrl,
    [string]$PublicUrl,
    [string]$Cookies = (Join-Path $PSScriptRoot 'out\cookies.txt'),
    # Empty = yt-dlp's default selection (what the plan's "Best" preset uses). E.g. '616+bestaudio' to force Premium.
    [string]$PremiumFormat = '',
    [string]$YtDlp = $(if (Get-Command yt-dlp -ErrorAction SilentlyContinue) { (Get-Command yt-dlp).Source } else { 'C:\tools\yt-dlp.exe' }),
    [switch]$ListOnly,
    [switch]$NoCookies
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$env:PYTHONUTF8 = '1'

$cases = @(
    @{ Label = 'members'; Url = $MembersUrl; Format = '' }
    @{ Label = 'premium'; Url = $PremiumUrl; Format = $PremiumFormat }
    @{ Label = 'public';  Url = $PublicUrl;  Format = '' }
) | Where-Object { $_.Url }

if (-not $cases) { throw 'Pass at least one of -MembersUrl, -PremiumUrl, -PublicUrl.' }
if (-not $NoCookies -and -not (Test-Path $Cookies)) { throw "Cookies file not found: $Cookies (run the harness first)" }

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$logDir = Join-Path $PSScriptRoot 'logs'
$outDir = Join-Path $PSScriptRoot 'out'
$dlDir = Join-Path $outDir 'downloads'
New-Item -ItemType Directory -Force $logDir, $dlDir | Out-Null

Write-Host "yt-dlp: $YtDlp ($(& $YtDlp --version))"
$results = foreach ($case in $cases) {
    $label = $case.Label
    $common = @('--ignore-config', '-v', '--js-runtimes', 'deno')
    if ($NoCookies) {
        $label = "$label-nocookies"
    } else {
        # yt-dlp writes the jar back to --cookies on exit; give each case its own copy so the export stays pristine.
        $jar = Join-Path $outDir "cookies-$label-$stamp.txt"
        Copy-Item $Cookies $jar
        $common += @('--cookies', $jar)
    }

    Write-Host "`n=== $label : formats ===" -ForegroundColor Cyan
    $formatsLog = Join-Path $logDir "$stamp-$label-formats.log"
    & $YtDlp @common -F $case.Url *>&1 | Tee-Object -FilePath $formatsLog
    $formatsExit = $LASTEXITCODE

    $downloadExit = $null
    $downloadLog = $null
    if (-not $ListOnly) {
        Write-Host "`n=== $label : download ===" -ForegroundColor Cyan
        $downloadLog = Join-Path $logDir "$stamp-$label-download.log"
        $dlArgs = $common + @('-P', $dlDir, '-o', "$label-%(id)s.%(ext)s")
        if ($case.Format) { $dlArgs += @('-f', $case.Format) }
        & $YtDlp @dlArgs $case.Url *>&1 | Tee-Object -FilePath $downloadLog
        $downloadExit = $LASTEXITCODE
    }

    $text = (Get-Content (@($formatsLog, $downloadLog) | Where-Object { $_ }) -Raw) -join "`n"
    [pscustomobject]@{
        Case          = $label
        FormatsExit   = $formatsExit
        DownloadExit  = $downloadExit
        PremiumRows   = ([regex]::Matches($text, '(?im)^\S+.*premium.*$') | Where-Object { $_.Value -notmatch '^\[' }).Count
        Selected      = ([regex]::Match($text, '(?m)Downloading \d+ format\(s\): (\S+)')).Groups[1].Value
        PoTokenWarns  = ([regex]::Matches($text, '(?im)^(WARNING|ERROR):.*po[ _-]?token.*$')).Count
        JsRuntimes    = ([regex]::Match($text, '(?m)^\[debug\] JS runtimes: (.*)$')).Groups[1].Value
        Errors        = (([regex]::Matches($text, '(?m)^ERROR: .*$') | ForEach-Object Value) -join ' | ')
    }
}

Write-Host "`n=== Summary ===" -ForegroundColor Cyan
$results | Format-List
Write-Host "Logs: $logDir"
Write-Host "Check the logs for PO-token warnings before deleting them; they may contain video titles but no cookie values."
