param(
    [string]$Name = "YouTube Download Helper"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$distDir = Join-Path $root "dist"
$buildDir = Join-Path $root "build"
$specDir = Join-Path $buildDir "spec"
$bundleDir = Join-Path $distDir $Name

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

Write-Host "Portable app built at $bundleDir"
