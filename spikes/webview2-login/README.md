# Spikes #31 and #35: WebView2 Google sign-in → cookies.txt → yt-dlp, and refresh by injection

Throwaway harness, not part of the solution. Results go in `docs/spikes/webview2-login.md`.

`profile/`, `profile-inject/`, `out/` and `logs/` are git-ignored. `out/` holds **live session cookies**, so never commit or share it,
and delete it (plus `profile/` and `profile-inject/`) when the spike is done.

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
`-Cookies <file>` picks a different jar.

## 3. Refresh by injection (#35)

Results go in `docs/spikes/cookie-refresh.md`.

```powershell
Copy-Item out\cookies.txt out\cookies-gen0.txt
dotnet run -- --inject out\cookies-gen0.txt --out out\cookies-gen1.txt   # fresh profile-inject\, youtube.com, 20 s
./cookie-diff.ps1 -Before out\cookies-gen0.txt -After out\cookies-gen1.txt
./lifetime-check.ps1 -MembersUrl <url>      # members-only check of every out\cookies-genN.txt; appends logs\lifetime.csv
```

`--inject` deletes `profile-inject\` first, injects the jar, opens `https://www.youtube.com/`, waits `--wait`
seconds (default 20), logs YouTube's own `LOGGED_IN` flag, and exports. The log lists `accounts.*` requests
(`RotateCookies`) and which cookies were added, changed or deleted, by name only. `cookie-diff.ps1` compares two
Netscape files by domain + path + name and prints status, expiry and value length, never values.

Every refresh **rotates** the session; a jar more than one rotation behind is dead. Always refresh from the newest
jar, and don't open `profile\` or `profile-inject\` again, since they hold live copies of the session.
