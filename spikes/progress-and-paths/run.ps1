# Throwaway spike for issue #33: run yt-dlp the way Engine.exe will (ProcessStartInfo.ArgumentList, explicit
# environment, stdout/stderr captured as raw bytes) with non-ASCII tool, work and destination paths.
# Each run writes out\<case>\{args.json,stdout.bin,stderr.bin,exit.txt} and the downloaded files under work\.
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Case,
    [Parameter(Mandatory)] [string]$Url,
    # Extra yt-dlp arguments for this case (format, audio extraction, ...).
    [string[]]$Extra = @(),
    # How the child's stdout encoding is set: none (scrubbed env), pythonutf8, pythonioencoding, encodingflag, all.
    [ValidateSet('none', 'pythonutf8', 'pythonioencoding', 'encodingflag', 'all')] [string]$EncodingMode = 'all',
    # Prefix the title with Turkish, emoji and CJK characters (affects the filename and the embedded metadata).
    [switch]$UnicodeTitle,
    [switch]$NoEmbed,
    [switch]$NoProgressTemplate,
    # Run the tools from the ASCII folder instead of the non-ASCII copy.
    [switch]$AsciiTools,
    # --progress-delta in seconds; empty = yt-dlp's default (a line for every update).
    [string]$ProgressDelta = ''
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# Non-ASCII everywhere. $uniRoot stands in for a Windows profile with a non-ASCII user name (C:\Users\Şükrü...):
# the plan's data folder (tools, work, cache) and the default download folder both live under it.
$uniRoot = Join-Path $root 'work\Kullanıcı İ 😀 用户'
$dataDir = Join-Path $uniRoot 'AppData\Local\YoutubeDownloadHelperData'
$toolsSrc = Join-Path $root 'tools'
$toolsDir = if ($AsciiTools) { $toolsSrc } else { Join-Path $dataDir 'tools' }
if (-not $AsciiTools -and -not (Test-Path $toolsDir)) {
    New-Item -ItemType Directory -Force $toolsDir | Out-Null
    Copy-Item -Recurse (Join-Path $toolsSrc 'yt-dlp'), (Join-Path $toolsSrc 'deno') $toolsDir
    New-Item -ItemType Directory -Force (Join-Path $toolsDir 'ffmpeg') | Out-Null
    Copy-Item (Join-Path $toolsSrc 'ffmpeg\ffmpeg-master-latest-win64-gpl\bin\ff*.exe') (Join-Path $toolsDir 'ffmpeg')
}
$ytdlp = Join-Path $toolsDir 'yt-dlp\yt-dlp.exe'
$deno = Join-Path $toolsDir 'deno\deno.exe'
$ffmpegDir = if ($AsciiTools) { Join-Path $toolsDir 'ffmpeg\ffmpeg-master-latest-win64-gpl\bin' } else { Join-Path $toolsDir 'ffmpeg' }

$itemId = [guid]::NewGuid().ToString('N').Substring(0, 8)
$tempDir = Join-Path $dataDir "work\$itemId"
$homeDir = Join-Path $uniRoot "Downloads\Youtube İndirgeç\$Case"
$cacheDir = Join-Path $dataDir 'cache\yt-dlp'
if (Test-Path $homeDir) { Remove-Item -Recurse -Force $homeDir }
New-Item -ItemType Directory -Force $tempDir, $homeDir, $cacheDir | Out-Null

$outDir = Join-Path $root "out\$Case"
if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }
New-Item -ItemType Directory -Force $outDir | Out-Null

$prefix = 'YDH1 '
# %(...)j is json.dumps with ensure_ascii, so these lines are pure ASCII whatever the output encoding.
$dlTemplate = 'download:' + $prefix + '{"k":"dl","p":%(progress.{status,downloaded_bytes,total_bytes,total_bytes_estimate,speed,eta,elapsed,fragment_index,fragment_count})j,"i":%(info.{id,format_id})j}'
$planPrint = 'before_dl:' + $prefix + '{"k":"plan","i":%(.{id,title,channel,format_id,ext,filesize,filesize_approx})j,"rf":%(requested_formats.:.{format_id,ext,protocol,filesize,filesize_approx})j}'
$ppTemplate = 'postprocess:' + $prefix + '{"k":"pp","p":%(progress.{status,postprocessor})j,"i":%(info.{id,filepath})j}'
$movedPrint = 'after_move:' + $prefix + '{"k":"moved","i":%(.{id,filepath})j}'

$args_ = [Collections.Generic.List[string]]::new()
$args_.AddRange([string[]]@(
    '--ignore-config', '--no-playlist', '--newline', '--progress',
    '--cache-dir', $cacheDir,
    '--ffmpeg-location', $ffmpegDir,
    '--js-runtimes', "deno:$deno",
    '-P', "temp:$tempDir", '-P', "home:$homeDir",
    '-o', '%(channel)s\%(upload_date>%Y-%m-%d)s - %(title)s.%(ext)s'))
if ($ProgressDelta) { $args_.AddRange([string[]]@('--progress-delta', $ProgressDelta)) }
if (-not $NoProgressTemplate) { $args_.AddRange([string[]]@('--progress-template', $dlTemplate, '--progress-template', $ppTemplate)) }
$args_.AddRange([string[]]@('--print', $planPrint, '--print', $movedPrint))
if (-not $NoEmbed) { $args_.AddRange([string[]]@('--embed-metadata', '--embed-thumbnail', '--embed-chapters')) }
if ($UnicodeTitle) { $args_.AddRange([string[]]@('--replace-in-metadata', 'title', '^', 'İzmir ılık 😀 東京 — ')) }
if ($EncodingMode -in 'encodingflag', 'all') { $args_.AddRange([string[]]@('--encoding', 'utf-8')) }
if ($Extra) { $args_.AddRange([string[]]$Extra) }
$args_.Add($Url)

$psi = [Diagnostics.ProcessStartInfo]::new($ytdlp)
foreach ($a in $args_) { $psi.ArgumentList.Add($a) }
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.WorkingDirectory = $tempDir
# Start from a scrubbed environment: the user's PYTHON* variables must not leak into the child.
foreach ($k in @($psi.Environment.Keys)) { if ($k -like 'PYTHON*') { $psi.Environment.Remove($k) | Out-Null } }
if ($EncodingMode -in 'pythonutf8', 'all') { $psi.Environment['PYTHONUTF8'] = '1' }
if ($EncodingMode -in 'pythonioencoding', 'all') { $psi.Environment['PYTHONIOENCODING'] = 'utf-8' }

[ordered]@{
    case = $Case; url = $Url; encodingMode = $EncodingMode; exe = $ytdlp; args = $args_
    env = [ordered]@{ PYTHONUTF8 = $psi.Environment['PYTHONUTF8']; PYTHONIOENCODING = $psi.Environment['PYTHONIOENCODING'] }
    tempDir = $tempDir; homeDir = $homeDir
} | ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8NoBOM (Join-Path $outDir 'args.json')

$p = [Diagnostics.Process]::Start($psi)
$so = [IO.File]::Create((Join-Path $outDir 'stdout.bin'))
$se = [IO.File]::Create((Join-Path $outDir 'stderr.bin'))
$t1 = $p.StandardOutput.BaseStream.CopyToAsync($so)
$t2 = $p.StandardError.BaseStream.CopyToAsync($se)
$p.WaitForExit()
[Threading.Tasks.Task]::WaitAll($t1, $t2)
$so.Dispose(); $se.Dispose()
"$($p.ExitCode)" | Set-Content (Join-Path $outDir 'exit.txt')

Write-Host "[$Case] exit=$($p.ExitCode) stdout=$((Get-Item (Join-Path $outDir 'stdout.bin')).Length)B stderr=$((Get-Item (Join-Path $outDir 'stderr.bin')).Length)B"
Write-Host "temp dir after run: $(@(Get-ChildItem -Recurse -File $tempDir).Count) file(s)"
Get-ChildItem -Recurse -File $homeDir | ForEach-Object { Write-Host "  home: $($_.Name) ($($_.Length) B)" }
