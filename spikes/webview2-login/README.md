# Spike #31: WebView2 Google sign-in → cookies.txt → yt-dlp

Throwaway harness, not part of the solution. Results go in `docs/spikes/webview2-login.md`.

`profile/`, `out/` and `logs/` are git-ignored. `out/` holds **live session cookies**, so never commit or share it,
and delete it (plus `profile/`) when the spike is done.

## 1. Sign in and export

```powershell
dotnet run -- --fresh
```

A console window (log) and a browser window open on Google sign-in (`continue=https://www.youtube.com`).
Sign in normally. When `LOGIN_INFO` and `SAPISID` appear on `.youtube.com`, the harness waits 3 s, writes
`out\cookies.txt` (Netscape) and `out\cookies.report.txt` (names/flags only), and closes the WebView2.

If Google shows "This browser or app may not be secure", note the exact page and try:

```powershell
dotnet run -- --fresh --user-agent "<UA string>"
dotnet run -- --fresh --browser-args "--disable-blink-features=AutomationControlled"
dotnet run -- --fresh --start-url "https://www.youtube.com/"     # sign in from YouTube's own button
```

`--manual` disables auto-export (use the **Export now** button), `--keep-open` keeps the window after export.

## 2. Download

```powershell
./download.ps1 -MembersUrl <url> -PremiumUrl <url> -PublicUrl <url>
./download.ps1 -PremiumUrl <url> -PremiumFormat '616+bestaudio'   # force the Premium format if default skips it
./download.ps1 -MembersUrl <url> -NoCookies -ListOnly             # control: should fail without cookies
```

Runs yt-dlp with `--ignore-config -v --js-runtimes deno --cookies <copy>`: first `-F`, then a download.
Each case gets its own copy of the jar, because yt-dlp writes cookies back on exit. Logs: `logs\`.
