param(
    [string]$Name = "YouTube Download Helper"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$distDir = Join-Path $root "dist"
$buildDir = Join-Path $root "build"
$specDir = Join-Path $buildDir "spec"
$ffmpegCmd = Get-Command ffmpeg -ErrorAction Stop
$ffprobeCmd = Get-Command ffprobe -ErrorAction Stop
$bundleDir = Join-Path $distDir $Name
$bundleFfmpegDir = Join-Path $bundleDir "ffmpeg"

New-Item -ItemType Directory -Force -Path $specDir | Out-Null

python -m PyInstaller `
  --noconfirm `
  --clean `
  --windowed `
  --name $Name `
  --distpath $distDir `
  --workpath $buildDir `
  --specpath $specDir `
  --paths (Join-Path $root "src") `
  --collect-all yt_dlp `
  (Join-Path $root "src\ytdlp_helper\__main__.py")

New-Item -ItemType Directory -Force -Path $bundleFfmpegDir | Out-Null
Copy-Item -LiteralPath $ffmpegCmd.Source -Destination (Join-Path $bundleFfmpegDir "ffmpeg.exe") -Force
Copy-Item -LiteralPath $ffprobeCmd.Source -Destination (Join-Path $bundleFfmpegDir "ffprobe.exe") -Force

Write-Host "Portable app built at $bundleDir"
