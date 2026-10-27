# Spike #32: Velopack package with App.exe + Engine.exe and a non-ASCII title

Throwaway harness, not part of the solution. Results go in `docs/spikes/velopack.md`.

`publish/` and `feed/` are git-ignored. Installing creates `%LOCALAPPDATA%\YoutubeDownloadHelper\` (the real
app's install root), shortcuts named "Youtube İndirgeç", and toast registrations. Uninstall when done (step 4).

## 1. Build and pack

```powershell
dotnet tool install -g vpk
./build.ps1 -Version 1.0.0
```

vpk 1.2.161 fails on the **second** version's delta because of the non-ASCII title (velopack#1105). Either:

```powershell
./build.ps1 -Version 1.0.1 -Delta none
dotnet tool install vpk --version 1.3.39-g8d2844f --tool-path .\vpk13; ./build.ps1 -Version 1.0.1 -Vpk .\vpk13\vpk.exe
```

## 2. Install v1

```powershell
feed\YoutubeDownloadHelper-win-Setup.exe --silent
start shell:AppsFolder\velopack.YoutubeDownloadHelper
```

The app starts `Engine.exe` if it does not answer on `127.0.0.1:47123`.

## 3. Update

Pack a higher version (step 1), then use the buttons in the app or drive them from a terminal:

```powershell
./click.ps1 'Check + download update'
./click.ps1 'Apply (graceful engine stop)'     # or 'Apply (engine left running)'
./click.ps1 'Toast'
```

Logs: `%LOCALAPPDATA%\YdhVelopackSpike\{app,engine}.log` and `%LOCALAPPDATA%\velopack\velopack_YoutubeDownloadHelper.log`.

## 4. Uninstall

```powershell
& "$env:LOCALAPPDATA\YoutubeDownloadHelper\Update.exe" --uninstall --silent
```

Then optionally remove the toast leftovers (`HKCU:\Software\Classes\AppUserModelId\velopack.YoutubeDownloadHelper`,
`%LOCALAPPDATA%\ToastNotificationManagerCompat\Apps\velopack.YoutubeDownloadHelper`) and `%LOCALAPPDATA%\YdhVelopackSpike`.
