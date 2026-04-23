param(
    [string]$Name = "YouTube Download Helper",
    [string]$YtDlpPath = ""
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$distDir = Join-Path $root "dist"
$buildDir = Join-Path $root "build"
$specDir = Join-Path $buildDir "spec"
$ffmpegCmd = Get-Command ffmpeg -ErrorAction Stop
$ffprobeCmd = Get-Command ffprobe -ErrorAction Stop
$vendorYtDlp = Join-Path $root "vendor\yt-dlp.exe"
$vendorNestedYtDlp = Join-Path $root "vendor\yt-dlp\yt-dlp.exe"
$bundleDir = Join-Path $distDir $Name
$bundleFfmpegDir = Join-Path $bundleDir "ffmpeg"

function Resolve-YtDlpPath {
    param([string]$ExplicitPath)

    if ($ExplicitPath) {
        if (-not (Test-Path -LiteralPath $ExplicitPath)) {
            throw "yt-dlp executable was not found at $ExplicitPath"
        }
        return (Resolve-Path -LiteralPath $ExplicitPath).Path
    }

    if (Test-Path -LiteralPath $vendorYtDlp) {
        return (Resolve-Path -LiteralPath $vendorYtDlp).Path
    }
    if (Test-Path -LiteralPath $vendorNestedYtDlp) {
        return (Resolve-Path -LiteralPath $vendorNestedYtDlp).Path
    }

    $commands = Get-Command yt-dlp.exe, yt-dlp -All -ErrorAction SilentlyContinue
    foreach ($command in $commands) {
        if ($command.Source -and $command.Source -notmatch "\\Python\d*\\Scripts\\") {
            return $command.Source
        }
    }

    throw "Could not find a standalone yt-dlp.exe. Put yt-dlp.exe in vendor\, pass -YtDlpPath, or install a standalone executable on PATH."
}

$ytDlpExe = Resolve-YtDlpPath -ExplicitPath $YtDlpPath

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
  (Join-Path $root "src\ytdlp_helper\__main__.py")

New-Item -ItemType Directory -Force -Path $bundleFfmpegDir | Out-Null
Copy-Item -LiteralPath $ytDlpExe -Destination (Join-Path $bundleDir "yt-dlp.exe") -Force
Copy-Item -LiteralPath $ffmpegCmd.Source -Destination (Join-Path $bundleFfmpegDir "ffmpeg.exe") -Force
Copy-Item -LiteralPath $ffprobeCmd.Source -Destination (Join-Path $bundleFfmpegDir "ffprobe.exe") -Force

Write-Host "Portable app built at $bundleDir"
