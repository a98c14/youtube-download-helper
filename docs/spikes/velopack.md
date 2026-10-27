# Spike: Velopack package with two executables and a non-ASCII title

Issue #32 · run 2026-10-27 · harness: `spikes/velopack/` (throwaway, not part of the solution)

Plan references: §9 (distribution and updates), §8 (data folder), §7.3 (toasts), §7.4 (engine supervision),
§12 M0 (d).

## Result

| Question | Answer |
|---|---|
| One package with `App.exe` + `Engine.exe`? | **Yes.** Both published self-contained into one folder, `--mainExe App.exe`. |
| Updates cleanly while the engine is running? | **Yes.** Graceful path: `POST /shutdown`, wait for exit, apply. Without that, Velopack **hard-kills** the engine (no shutdown code runs) and the update still succeeds. |
| Both exes update? | **Yes.** After each update the app and the restarted engine both report the new version (1.0.0 → 1.0.1 → 1.0.2 → 1.0.3). |
| Non-ASCII title: shortcuts, Add/Remove Programs, toasts | **Works.** `Youtube İndirgeç.lnk`, the Apps list name and the toast header all show the title correctly. |
| Non-ASCII title: `vpk pack` | **Bug.** vpk 1.2.161 (current stable) fails to build **delta** packages ([velopack#1105](https://github.com/velopack/velopack/issues/1105)). Fixed in prerelease 1.3.39; `--delta none` works around it on stable. |
| Data folder survives uninstall? | **No, if it lives in the install root.** Uninstall deletes all of `%LOCALAPPDATA%\YoutubeDownloadHelper\`, including files Velopack did not create. The plan used that same path for `state.db`; see [Decisions](#decisions-for-the-plan). |
| Toast library | **`Microsoft.Toolkit.Uwp.Notifications` 7.1.3** (see [Toasts](#toasts)). |

## Environment

| | |
|---|---|
| OS | Windows 11 Pro 10.0.26200, x64, ANSI code page 1252 |
| .NET | SDK 10.0.403, self-contained `win-x64` |
| Velopack library | `Velopack` 1.2.161 (NuGet) |
| vpk | 1.2.161 (stable) for v1.0.0; 1.3.39-g8d2844f (prerelease) for 1.0.1–1.0.3 |
| Toasts | `Microsoft.Toolkit.Uwp.Notifications` 7.1.3, App TFM `net10.0-windows10.0.19041.0` |

## Harness

- `App/`: WPF `App.exe`. Calls `VelopackApp.Build()…Run()` first in `Main` and logs every hook. It starts
  `Engine.exe` if `/info` does not answer, as in §7.4, and has buttons for check/download, apply (with and without
  stopping the engine), and toast.
- `Engine/`: headless `Engine.exe` (`WinExe`, no window). It has a named mutex, `GET /info` and
  `POST /shutdown` on `127.0.0.1:47123`, a heartbeat log, and a 1 s "drain" before it exits cleanly.
- `build.ps1 -Version x.y.z [-Vpk <path>] [-Delta none]`: publishes both, then runs `vpk pack` into `feed\`, a local
  folder used as the update feed (`UpdateManager(<folder>)`).
- `click.ps1 '<button>'`: clicks a button through UI Automation, so the whole flow can run from a terminal.
- Logs: `%LOCALAPPDATA%\YdhVelopackSpike\{app,engine}.log`. Velopack's own log:
  `%LOCALAPPDATA%\velopack\velopack_YoutubeDownloadHelper.log`.

## vpk commands

```powershell
dotnet publish App\App.csproj       -c Release -r win-x64 --self-contained -o publish\$v -p:Version=$v
dotnet publish Engine\Engine.csproj -c Release -r win-x64 --self-contained -o publish\$v -p:Version=$v

vpk pack --packId YoutubeDownloadHelper --packTitle 'Youtube İndirgeç' --packAuthors a98c14 `
    --packVersion $v --packDir publish\$v --mainExe App.exe --runtime win-x64 --outputDir feed
```

- Publishing both projects into the same folder works: the shared runtime files are identical, and each exe keeps
  its own `.deps.json` / `.runtimeconfig.json`.
- vpk generates `<shortcutAumid>velopack.YoutubeDownloadHelper</shortcutAumid>` in the nuspec. `--aumid` (a
  hidden option, `vpk pack -H`) overrides it. The default is fine.
- Sizes: full package 74 MB (both exes, self-contained WPF + Windows SDK projection), Setup.exe 82 MB,
  delta 1.0.0 → 1.0.1 **2 MB** (9 of 409 files patched).
- Silent install: `Setup.exe --silent`. It installs to `%LOCALAPPDATA%\YoutubeDownloadHelper\` but does not start
  the app (only the `--veloapp-install` hook runs). Silent uninstall:
  `%LOCALAPPDATA%\YoutubeDownloadHelper\Update.exe --uninstall --silent`.

### The non-ASCII title breaks delta packages on vpk 1.2.161

Since vpk 1.2.158 the launcher stub is named after `--packTitle` (`Youtube İndirgeç_ExecutionStub.exe`, which
installs as `%LOCALAPPDATA%\YoutubeDownloadHelper\Youtube İndirgeç.exe`). Packing the **second** release diffs that
stub with the bundled `zstd.exe`, which reads its arguments in the ANSI code page:

```
[ERR] Failed to create zstd diff for file '…\lib\app\Youtube İndirgeç_ExecutionStub.exe'.
zstd: error 31 : Stat failed on dictionary file …\lib\app\Youtube Indirge�_ExecutionStub.exe: No such file or directory
[FTL] Delta creation failed for one or more files. … use the '--delta none' argument.
```

The first release packs fine because there is nothing to diff, so this shows up only on the first update. It
depends on the build machine's code page: `İ` is not in 1252, which is what GitHub's `windows-latest` uses. This
is upstream [velopack#1105](https://github.com/velopack/velopack/issues/1105), fixed by
[velopack#1113](https://github.com/velopack/velopack/pull/1113) (merged 2026-10-17), which so far ships only in
prerelease **1.3.39-g8d2844f**. With that vpk, the same commands built correct deltas for 1.0.1, 1.0.2 and 1.0.3,
and the installed app applied them.

Options until a stable release includes the fix:
1. Stable vpk 1.2.161 with `--delta none`: every update downloads the full ~74 MB package.
2. vpk 1.3.39 prerelease. The library and vpk should normally be the same version. In this run, packages from
   vpk 1.3.39 (with its `Update.exe`) were applied by an app on library 1.2.161 with no problem, but that mix
   was only tested here.

Recommendation: **option 1 for now** (a few friends, infrequent updates), and remove `--delta none` once a stable
vpk includes #1113. Keep `Velopack` and `vpk` pinned to the same version.

## Update flow and the running engine

Sequence observed with **Apply (graceful engine stop)**:

```
app 1.0.0   POST /shutdown → engine logs "shutdown requested", drains, "clean exit"; app waits for the pid
app 1.0.0   UpdateManager.ApplyUpdatesAndRestart(update)
Update.exe  waits for App.exe, force-stop check (nothing left from the install dir), swaps current\
app 1.0.0   --veloapp-obsolete 1.0.0  → before-update hook (old version)
app 1.0.1   --veloapp-updated 1.0.1   → after-update hook (new version)
app 1.0.1   normal start, OnRestarted fires, supervisor finds no engine and starts Engine.exe 1.0.1
```

With **Apply (engine left running)**, Velopack's `Update.exe` scans for processes whose image lives anywhere
under the **install root** and kills them before it swaps `current\`:

```
[update] Checking for running processes in: C:\Users\…\AppData\Local\YoutubeDownloadHelper
[update] Inspected 298 running processes in 48ms, 2 matched directory
[update] WARN Killing process: C:\Users\…\AppData\Local\YoutubeDownloadHelper\current\Engine.exe (21376)
```

The engine's log stops at the last heartbeat. No `ProcessExit`, no drain: it was a hard kill. The update still
succeeded, and the new app started a 1.0.2 engine.

So:
- **The engine must be shut down gracefully by the app before apply** (§9 already says `POST /engine/shutdown`).
  Velopack's kill is only a safety net for when that fails. A hard kill mid-download is recoverable by design
  (§11 crash recovery), but it skips the clean SQLite checkpoint and leaves job state for recovery to fix.
- The scan covers **the whole root**, not just `current\`. Any `yt-dlp.exe`/`ffmpeg`/`deno` run from a `tools\`
  folder inside the root would be killed too (moot once the data folder moves; see below).
- With "keep downloading after Exit" on, the app may not be running when an update is wanted. Updates are only
  applied from the app, which stops the engine first, so this holds.
- Velopack calls the hooks with the **old** exe (`--veloapp-obsolete`) and then the **new** one
  (`--veloapp-updated`), each as a short-lived process. `VelopackApp.Run()` must be the first thing in `Main`.
  `Engine.exe` needs no Velopack code.

## Shortcuts, AUMID and the non-ASCII title

| Item | Observed |
|---|---|
| Start-menu shortcut | `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Youtube İndirgeç.lnk` → `…\current\App.exe`, working dir `current\`, AUMID `velopack.YoutubeDownloadHelper` |
| Desktop shortcut | `Desktop\Youtube İndirgeç.lnk`, same target and AUMID. Default `--shortcuts Desktop,StartMenuRoot`. |
| `Get-StartApps` | `Youtube İndirgeç  velopack.YoutubeDownloadHelper`; `shell:AppsFolder\velopack.YoutubeDownloadHelper` launches the app |
| Add/Remove Programs | key `HKCU\…\Uninstall\YoutubeDownloadHelper`, DisplayName `Youtube İndirgeç` |
| Process AUMID | `velopack.YoutubeDownloadHelper`. `VelopackApp.Run()` sets it explicitly ("Setting current process explicit AppUserModelID"), so it also applies when the app starts from the Run key, a restart after an update, or `App.exe` directly. |
| Updates | Velopack finds the existing shortcuts by target and rewrites them on every update. Name and AUMID stay the same. |

The shortcut target is `current\App.exe`, a path that stays the same across updates, so the autostart Run key
(`App.exe --minimized`) can point there too.

Tooling note: `WScript.Shell.CreateShortcut()` returns an empty `TargetPath` for the non-ASCII `.lnk` in
PowerShell. `Shell.Application` (`ParseName(...).GetLink`) reads it correctly. This only matters for scripts and
tests.

## Toasts

Library: **`Microsoft.Toolkit.Uwp.Notifications` 7.1.3** (`ToastContentBuilder` + `ToastNotificationManagerCompat`).
- Works in an unpackaged, self-contained .NET 10 WPF app. It requires a Windows TFM
  (`net10.0-windows10.0.17763.0` or later; the spike used `10.0.19041.0`).
- It picks up the process AUMID set by Velopack and registers it under
  `HKCU\Software\Classes\AppUserModelId\velopack.YoutubeDownloadHelper` with `DisplayName = Youtube İndirgeç`, plus
  a COM activator `"…\current\App.exe" -ToastActivated`. That path is also stable across updates.
- The toast rendered with the header **"Youtube İndirgeç"** and a Turkish body (`İndirme tamamlandı`,
  `Çalışma ağacı — ğüşöçıİ`) intact. A click while the app was running raised `OnActivated` with the arguments.
  It showed the generic icon because the spike has none. The real app sets `--icon`.
- Leftovers after uninstall: the `AppUserModelId` key, the CLSID, and
  `%LOCALAPPDATA%\ToastNotificationManagerCompat\Apps\velopack.YoutubeDownloadHelper\`. The uninstall hook should call
  `ToastNotificationManagerCompat.Uninstall()`.

Why not the alternatives:
- Windows App SDK `AppNotificationManager`: unpackaged apps need the Windows App Runtime installed, or the
  self-contained Windows App SDK. That is extra size and moving parts for the same result.
- Raw WinRT `ToastNotificationManager`: it works with the same AUMID, but we would rewrite the registration and
  the COM activation that the toolkit already does.
- Caveat: the toolkit gets no new features (the CommunityToolkit 8.x line does not include it), but it is stable
  and its API is the standard WinRT one, so it can be replaced later.

## Uninstall and the data folder

Fake `state.db`, `logs\engine.log` and `tools\yt-dlp\yt-dlp.exe` were placed in `%LOCALAPPDATA%\YoutubeDownloadHelper\`
(the plan's §8 data folder, which is also Velopack's install root):

- **Updates keep them.** Velopack only replaces `current\`, `packages\`, `Update.exe` and the stub.
- **Uninstall deletes the whole folder**, including those files. The `--veloapp-uninstall` hook ran first.

Hooks (`On…FastCallback`) are killed after **30 s**, then `Environment.Exit` is called, and uninstall can run
silently from Settings → Apps. A "delete your data?" prompt inside the uninstall hook is therefore unreliable.

## Not tested

- Toast click **while the app is not running** (COM activation launching `App.exe -ToastActivated`). This needs a
  human click on a toast in Action Center after exiting the app. `VelopackApp.Run()` must tolerate that argument.
- `vpk upload github` and GitHub Releases as the feed (M5 / CI). Only a local folder feed was tested.
- Update on a machine with a non-ASCII **Windows user name** (install path with non-ASCII characters).
  velopack#1113 says that also affected delta creation, but only on the build machine.

## Decisions for the plan

- §8: **move the data folder out of Velopack's root**, to `%LOCALAPPDATA%\YoutubeDownloadHelperData\`. The pack ID
  stays `YoutubeDownloadHelper`, so the install root stays `%LOCALAPPDATA%\YoutubeDownloadHelper\`.
- §9 packaging: `--mainExe App.exe`, both exes published into one folder, `--delta none` until a stable vpk includes
  velopack#1113, and `Velopack` and `vpk` pinned to the same version.
- §9 app updates: confirmed. The app must stop the engine (`POST /engine/shutdown`, wait for the process to exit)
  before `ApplyUpdatesAndRestart`, because Velopack otherwise hard-kills every process under the install root.
- §9 uninstall: the hook (≤ 30 s, no UI) removes the Run key and the toast registration. The data folder is
  **kept**, so a reinstall picks it up. Removing it is left to the user (Settings → Advanced → open data folder).
- §7.3 toasts: `Microsoft.Toolkit.Uwp.Notifications`, App TFM `net10.0-windows10.0.19041.0`, AUMID from Velopack
  (`velopack.YoutubeDownloadHelper`).
