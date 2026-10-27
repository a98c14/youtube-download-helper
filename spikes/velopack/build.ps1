# Builds version $Version of the spike into publish\<version> and packs it into feed\ with vpk.
# vpk 1.2.161 fails to build deltas with a non-ASCII title (velopack#1105); pass -Vpk to use a fixed build,
# or -Delta none.
param([Parameter(Mandatory)] [string] $Version, [string] $Vpk = 'vpk', [string] $Delta = 'BestSpeed')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root "publish\$Version"
$feed = Join-Path $root 'feed'
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue

# Both executables are published self-contained into the same folder.
dotnet publish "$root\App\App.csproj" -c Release -r win-x64 --self-contained -o $out -p:Version=$Version -p:FeedPath=$feed
if ($LASTEXITCODE) { throw 'publish App failed' }
dotnet publish "$root\Engine\Engine.csproj" -c Release -r win-x64 --self-contained -o $out -p:Version=$Version
if ($LASTEXITCODE) { throw 'publish Engine failed' }

& $Vpk pack --packId YoutubeDownloadHelper --packTitle 'Youtube İndirgeç' --packAuthors a98c14 `
    --packVersion $Version --packDir $out --mainExe App.exe --runtime win-x64 --outputDir $feed --delta $Delta
if ($LASTEXITCODE) { throw 'vpk pack failed' }
