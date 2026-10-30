# Youtube İndirgeç — Design Plan

Status: **Draft v1** · Created 2026-10-27 · Supersedes the prototype in `.archive/` (not used as a reference)

Naming: **"Youtube İndirgeç"** is the only user-facing name (window titles, shortcuts, installer, toasts, tray).
Internally we use `YoutubeDownloadHelper` (projects, assemblies, namespaces `YoutubeDownloadHelper.*`, data folder,
Velopack pack ID, executables `YoutubeDownloadHelper.exe` and `YoutubeDownloadHelper.Engine.exe`) and
`youtube-download-helper` (repo). All of these live in `YoutubeDownloadHelper.Contracts.AppIdentity`; never derive
paths or IDs from the display name (it contains non-ASCII characters). The `YDH1 ` prefix on yt-dlp progress lines
(§5.4) is a wire marker, not a project name.

A Windows-native, qBittorrent-style download manager for YouTube (and other yt-dlp–supported sites), built
around a headless engine that any UI or program can drive. Primary differentiator: reliable downloads of
**members-only** and **Premium** content using the user's own logged-in account.

---

## 1. Goals

1. Queue-based downloading with a dense, familiar table UI (qBittorrent-like).
2. Members-only and Premium content via cookies, obtained **without manual copy-pasting**.
3. Robustness: errors are classified, retried with backoff, never lost on crash/reboot; partial downloads resume.
4. Usable by **non-technical friends**: installer, first-run wizard, plain-language errors, self-managing dependencies.
5. Self-maintaining: yt-dlp / Deno / ffmpeg update silently (with rollback); the app updates with user consent.
6. **Core fully separate from UI**: a headless engine with a public local API, so future UIs, a CLI, and other
   programs can queue downloads.

## 2. Non-goals for v1 (deferred, but the architecture must allow them)

| Deferred to v2+ | Notes for the architecture |
|---|---|
| Livestream recording (wait for start, `--live-from-start`) | State machine reserves `WaitingForStream` / `Recording`. v1 has no special handling: live/upcoming URLs go through the normal pipeline, and whatever yt-dlp does or reports is shown. |
| Subtitles, SponsorBlock | Presets are an engine-side abstraction, so options can be added without API breaks. |
| Speed limits / schedules, concurrency UI | Scheduler already owns concurrency; limits plug into the runner's arguments. |
| `ydh` CLI, browser extension, LAN access | The HTTP API is the integration point; these are just clients. |
| Clipboard monitoring, drag & drop | UI-only features. |
| Multiple accounts | Auth store is single-account; schema keeps an `account_id` column to make migration easy. |
| Raw format picker | Presets only. |

## 3. Decisions log

| Area | Decision |
|---|---|
| Name | User-facing "Youtube İndirgeç"; internal `YoutubeDownloadHelper` (namespaces `YoutubeDownloadHelper.*`). |
| Stack | C# / .NET 10, WPF with the built-in Fluent theme (follows system light/dark). Windows 10 + 11, x64. |
| Process model | **Separate headless engine process** (`YoutubeDownloadHelper.Engine.exe`) + WPF client (`YoutubeDownloadHelper.exe`). |
| Transport | **Localhost HTTP (REST/JSON) + Server-Sent Events**, `127.0.0.1` only, bearer token. |
| Audience | Me + a few non-technical friends. One YouTube account per Windows user. |
| Cookies | Embedded WebView2 login in an isolated profile; cookies pushed to the engine; manual `cookies.txt` import as a fallback. |
| Input | Paste URLs into the Add dialog; playlists expand into a picker. |
| Watchers | Subscribe to a channel or playlist; new uploads (incl. members-only) auto-queue. |
| Quality | Presets only. |
| Extras (v1) | Embedded metadata, thumbnail, and chapters; a download archive that skips duplicates. |
| Files | `<Download folder>\<Channel>\<YYYY-MM-DD> - <Title>.<ext>`; per-watcher folder override. |
| Queue controls | Pause, resume, reorder. Concurrency fixed at 2 (settings file only). |
| Background | Tray icon, opt-in start with Windows, toast notifications. Exit stops the engine by default. |
| Updates | Tools update silently with rollback; app updates prompt first. Velopack + GitHub Releases (public repo). |
| yt-dlp channel | Stable by default; Settings toggle to switch to nightly (e.g. when stable misses a critical fix) and back. |
| Live/upcoming URLs | No special handling in v1: treated like any video; yt-dlp's behavior or error is shown. |
| Session longevity | **Keep the login alive as long as possible; re-login is the last resort.** Engine owns a single cookie lineage: yt-dlp writebacks merged in, periodic keep-alive pings, periodic hidden-browser refresh, one-step rollback (§5.7). |
| UI language | Turkish (default) and English from day one; switchable in Settings. The engine API is language-neutral (codes + args) (§7.5). |
| "Best" preset | yt-dlp's default format selection and container choice (simplest). |
| Plan location | This file. |

---

## 4. Architecture

### 4.1 Process model

```
┌─── YoutubeDownloadHelper.exe (WPF) ────┐        ┌───── YoutubeDownloadHelper.Engine.exe (headless) ─────┐
│ Main window · tray · toasts            │  HTTP  │ API layer (Kestrel, 127.0.0.1, bearer token)          │
│ WebView2 login window                  │ ─────> │   └─> Core application services                       │
│ Velopack app-update prompts            │  SSE   │        Queue actor · Scheduler · Job runner           │
│ Engine supervisor (start/restart)      │ <───── │        Watchers · Tool manager · Auth store           │
└────────────────────────────────────────┘        │        SQLite (single writer)                         │
                                                  │   └─> yt-dlp.exe / ffmpeg / deno  (in a Job Object)   │
Other programs / future CLI ───── HTTP ─────────> └───────────────────────────────────────────────────────┘
```

Hard rules:
- **Only the engine touches `state.db`, the tools, and the stored cookies.** Clients use the API only.
- **The engine is single-instance per Windows user** (named mutex). A second engine exits immediately.
- **The WPF project never references `Core`.** It depends only on `Contracts` + `Client`. An architecture test enforces this.

### 4.2 Solution layout

```
YoutubeDownloadHelper.slnx
global.json        SDK 10.0.403 (latestFeature), test runner Microsoft.Testing.Platform (xUnit v3).
src/
  YoutubeDownloadHelper.Contracts/  net10.0. DTOs, enums, event types, API route constants, AppIdentity.
                                    No dependencies. Shared by engine + .NET clients.
  YoutubeDownloadHelper.Core/       net10.0-windows. Engine logic: queue actor, state machine, scheduler, runner,
                                    classifier, watchers, tools, auth store, persistence (EF Core + SQLite).
                                    No ASP.NET, no UI.
  YoutubeDownloadHelper.Engine/     net10.0-windows, Web SDK WinExe → YoutubeDownloadHelper.Engine.exe. Generic
                                    host, Kestrel minimal-API endpoints → Core services, SSE, discovery file,
                                    single-instance mutex, logging.
  YoutubeDownloadHelper.Client/     net10.0. Typed .NET API client (HttpClient + SSE reader, reconnect/resync).
  YoutubeDownloadHelper.App/        net10.0-windows10.0.19041.0, WPF → YoutubeDownloadHelper.exe
                                    (CommunityToolkit.Mvvm): windows, view models, tray, toasts, WebView2 login,
                                    engine supervisor, Velopack integration.
tests/
  YoutubeDownloadHelper.Core.Tests/    Unit tests: state machine, classifier, retry policy, argument builder,
                                       watcher diffing.
  YoutubeDownloadHelper.Engine.Tests/  API integration tests via ASP.NET TestServer + fake yt-dlp.
  YoutubeDownloadHelper.Client.Tests/  API client tests (SSE reconnect/resync) against fake HTTP.
  YoutubeDownloadHelper.App.Tests/     View-model tests against a fake client.
  YoutubeDownloadHelper.ArchTests/     Dependency-rule tests (App ↛ Core, Core ↛ ASP.NET, etc.).
tools/
  YoutubeDownloadHelper.FakeYtDlp/  Console app that mimics yt-dlp output/exit codes per scenario (progress, auth
                                    error, 429, extractor failure, disk full, slow, hang). Used by integration tests
                                    and for UI development.
```

Core and Engine target `net10.0-windows` because Job Objects, DPAPI, file ACLs and named mutexes are Windows-only.
Test projects use the TFM of the project under test.

References: App → Contracts + Client only. Engine → Core + Contracts. Core → Contracts (Core may use Contracts
types such as enums, codes and the DTOs it returns; EF entities never become Contracts types). Client →
Contracts. Test projects may reference anything. The architecture tests check this against the restore graph,
transitive references included.

Build conventions: central package management (transitive pinning, no lock files), nullable enabled, warnings as
errors, `Directory.Build.props`. Details and code organisation: `CLAUDE.md`.

### 4.3 Engine internals

**Concurrency model:** one **queue actor** (a single-reader `Channel<Command>`) owns all queue state in memory
and is the only component that mutates items. API calls, job workers, watchers, and the tool manager send it
messages. This removes a whole class of race conditions (the prototype's cleanup bugs are the cautionary tale).
After each state transition the actor persists to SQLite, then publishes an event.

**Scheduler:** picks the next `Queued` item by position when a slot is free (max 2). Adds a random 5–20 s
delay between consecutive YouTube job starts to reduce bot-detection risk. Honors queue-level blocks (below).

**Queue-level blocks:** conditions that stop new starts without failing items, each shown in the UI status bar:
`PausedByUser`, `WaitingForNetwork` (several jobs fail with network errors → probe connectivity, resume
automatically), `DiskFull` (free space below the item's estimated size + margin), `ToolsMissing`
(first run, or a broken install that is being repaired).

---

## 5. Queue item lifecycle

### 5.1 States

```
            ┌──────────── Pause ───────────┐
            ▼                              │
 Added ─> Queued ─> Resolving ─> Downloading ─> PostProcessing ─> Completed
            ▲  ▲         │            │               │
            │  │         └──────┬─────┴───────────────┘
            │  │                ▼
            │  │        (error classifier)
            │  └─ timer ── WaitingRetry      ← transient (network, 5xx, 429, unknown)
            │  └─ login ── WaitingAuth       ← auth required/expired
            │  └─ update ─ WaitingToolFix    ← extractor broken (yt-dlp update triggered)
            └──── Retry ── Failed            ← permanent (removed/private/geo) or retries exhausted
                           Skipped           ← already in the archive (unless forced)
 Remove: hides the item from the queue and history (removed_at); its state is unchanged
 Reserved for v2: WaitingForStream, Recording
```

- **Resolving:** `yt-dlp -J --no-playlist` fetches metadata: title, channel, upload date, duration, availability
  (`public` / `subscriber_only` / `premium_only` / `needs_auth`), `live_status`, size estimate, and the final filename.
  `live_status` is stored and shown as a badge, but **does not change behavior in v1**. A live URL is downloaded
  however yt-dlp handles it (records from "now" until the stream ends); an upcoming one fails with yt-dlp's own
  message, run through the normal classifier. Known v1 limitation: pausing a live recording cannot resume it.
- **Pause:** kills the yt-dlp process tree and keeps the partial files. **Resume** reruns yt-dlp, which continues the download.
- **Crash recovery:** at engine start, items in `Resolving`/`Downloading`/`PostProcessing` go back to `Queued`
  (the event's reason says why) and resume from their partial files. Orphaned work folders with no matching item are cleaned up.
- Every transition is written to `item_events` (timestamp, from, to, reason, error category), which
  powers the details pane and diagnostics.

### 5.2 Error classification and retry policy

One classifier maps the yt-dlp exit code and `ERROR:` lines to a category. Its patterns live in one file and are
tested against captured real-world stderr fixtures.

| Category | Examples | Action |
|---|---|---|
| Transient | timeouts, connection reset, HTTP 5xx, HTTP 429, fragment failures | `WaitingRetry` with backoff **30 s → 2 m → 10 m → 30 m**, ±20% jitter, then `Failed` |
| Auth | "members-only", "join this channel", "sign in to confirm your age", Premium-only, bot check while logged in | `WaitingAuth`; trigger an auth verification; prompt "Log in again" if the cookies are invalid |
| Unavailable | removed, private, copyright claim, geo-blocked | `Failed` immediately, with a plain-language reason |
| Tool broken | "Unable to extract…", signature/nsig failures, JS runtime errors | `WaitingToolFix`; force a yt-dlp update check (at most once per hour); requeue after an update, else `Failed` after 24 h |
| Local I/O | disk full, access denied, path too long | Disk full → queue block `DiskFull`; others → `Failed` with an actionable message |
| Unknown | anything else | Treated as transient, at most 2 retries |

Every failure shows a **friendly message** with a "Details" expander containing the raw yt-dlp output.

### 5.3 Presets

Presets live in the engine (`PresetId` → yt-dlp format/sort/merge arguments); clients only see IDs and display names.

| Preset | Intent |
|---|---|
| Best quality (default) | yt-dlp's default format selection and container (no custom sort): highest quality available, including Premium formats when logged in; output may be MKV (with `--embed-thumbnail`, yt-dlp turns a WebM merge into MKV) |
| 1080p | ≤1080p, prefers MP4/H.264 for compatibility with any player |
| 720p | ≤720p, MP4 |
| Audio (M4A) | Best audio, no re-encode |
| Audio (MP3) | Best audio converted to MP3 |

The exact format strings are decided in M1 and covered by argument-builder snapshot tests.

### 5.4 yt-dlp runner

- Every invocation runs inside a **Windows Job Object** (`KILL_ON_JOB_CLOSE`), so yt-dlp and its ffmpeg children
  die with the engine. Cancel or pause kills the whole tree.
- Fixed arguments on every run: `--ignore-config` (the user's global yt-dlp config can never interfere),
  `--no-playlist` for single items, `--newline`, `--progress` (required: `--print` implies `--quiet`, which
  otherwise hides all progress), `--progress-delta 0.25`, machine-readable `--progress-template`s for `download:`
  and `postprocess:`, and `--print` lines for `before_dl:` (format plan) and `after_move:` (final path). Also a
  private `--cache-dir`, `--ffmpeg-location <ffmpeg dir>`, `--js-runtimes deno:<managed deno.exe>`,
  `--embed-metadata --embed-thumbnail --embed-chapters`, and `--encoding utf-8`.
- **Machine-readable lines** (M0 spike, `docs/spikes/progress-and-paths.md`; exact strings there): prefix `YDH1 `
  + one JSON object with kind `k` = `plan` | `dl` | `pp` | `moved`. `%(…)j` escapes non-ASCII, so these lines are
  pure ASCII. `pp` lines arrive on **stderr** and the others on stdout, so the runner parses both streams. Lines
  without the prefix are raw output (`WARNING:`/`ERROR:`) for the classifier. The final path comes from `moved`.
- **Encoding:** `--encoding utf-8` is required. Without it `yt-dlp.exe` writes cp1252 and silently drops `İ`, `ı`,
  emoji and CJK from raw output; `PYTHONUTF8` / `PYTHONIOENCODING` have no effect on the exe. Both streams are
  decoded as UTF-8 (with replacement). Inherited `PYTHON*` variables are removed from the child environment.
- **Paths:** temporary/partial files go to `data\work\<itemId>\` (`-P temp:`); the finished file moves to the
  destination. If the destination filename already exists, the engine adds ` [<videoId>]` to the output template.
  The engine checks this **before** starting yt-dlp: when the final file already exists, yt-dlp skips the
  download, re-runs the embeds and reports success (M0 spike).
- Progress lines are parsed and coalesced into **at most 4 updates per second per item**. Progress is kept in
  memory only; the DB stores the size estimate and, at the end, the final size.
- The last 200 lines of output are kept per run (DB, on the `item_events` row that ends the run) for the details
  pane, excluding `dl` progress lines;
  full logs go to `logs\jobs\`.
- **Our own archive** in SQLite (key: `extractor + video id`, matching yt-dlp's archive format) replaces
  `--download-archive`, so the archive is queryable and shared with watchers and the Add dialog. It is not a
  table: a video is archived when any item for it has a `file_path`, which is set on `Completed` and kept when
  the item is removed or re-queued. Items are therefore never hard-deleted.

### 5.5 Watchers

```
Watcher { id, url, channelId, title, kind (Channel|Playlist),
          include: { Videos, Shorts, PastStreams, MembersOnly },
          presetId, folderOverride?, intervalMinutes (default 60, min 30),
          enabled, createdAt, backfill (None | LastN), lastCheckedAt, lastError }
```

- **Poll:** `yt-dlp --flat-playlist -J --playlist-end 30` for each enabled tab: `/videos`, `/shorts`,
  `/streams` (only finished streams, `was_live`), plus the channel's members-only uploads playlist
  (`UUMO` + channel ID suffix) when logged in.
- **Diff:** candidates are compared against the archive, existing queue items, and `watcher_seen`. On creation
  we record the current entries as seen, so the back catalog is not downloaded unless backfill = LastN.
- New items are queued with the watcher's preset and folder. Polls are staggered across watchers with jitter.
  Auth failures on the members tab mark the watcher "needs login" and do not fail the whole check.
- **"Check now"** runs a poll immediately.

### 5.6 Tool manager

Manages **yt-dlp**, **Deno** (yt-dlp's JS runtime for YouTube challenges), and **ffmpeg/ffprobe** (yt-dlp's
FFmpeg builds).

- Layout: `tools\<tool>\<version>\…`; the "current" and "previous" versions are recorded in the DB, with no
  symlinks. A version is never replaced in place, so swapping is safe while jobs run: running jobs keep the
  old binary and new jobs get the new one.
- Install/update: download from GitHub releases → **verify the published SHA-256 checksum** → extract to the
  version folder → **smoke test** (`--version`; yt-dlp also simulates a stable public video when the network is
  up) → atomically switch "current" → garbage-collect versions that are neither current, previous, nor in use.
- Schedule: check at startup and every 24 h, plus forced checks on tool-broken errors.
- **yt-dlp channel** (Settings → Updates): **Stable** (default) or **Nightly**, from the separate
  `yt-dlp/yt-dlp-nightly-builds` releases. Switching installs the latest build of the chosen channel right away
  (same verify/smoke-test path). Switching from nightly back to stable is an intentional *downgrade* by version
  number, so the comparison is per channel, never "newer wins". The setting text explains when to use nightly:
  "only if downloads are broken and a fix hasn't reached Stable yet".
- **Rollback:** automatic when the smoke test fails; manual "Roll back yt-dlp" in Settings → Updates.
- Health check at startup: if a binary is missing (e.g. quarantined by antivirus) → queue block `ToolsMissing`
  and reinstall.
- PO-token provider plugin: **not planned**. The M0 spike confirmed that cookies + Deno are enough (yt-dlp
  2026.10.26, members-only download, no PO-token warnings; `docs/spikes/webview2-login.md`). If that changes,
  the tool manager gains a "plugin" tool type.

### 5.7 Auth (cookies)

**Login flow (UI side):**
1. The user clicks **Log in to YouTube** (wizard or Settings → Account). `YoutubeDownloadHelper.exe` opens a WPF window with
   WebView2, using a **dedicated user-data folder** (`data\webview-profile\`, separate from the user's real browser).
2. It navigates to the Google sign-in page with `continue=https://www.youtube.com`. The user signs in normally
   (2FA, passkeys, etc. are handled by Google's own pages). Google may insert interstitials (e.g. "add recovery
   options" on `gds.google.com`), so the window must not assume a fixed page sequence. The default WebView2 user
   agent works; no override is needed (M0 spike, `docs/spikes/webview2-login.md`).
3. The app detects that login finished when `CookieManager.GetCookiesAsync("https://www.youtube.com/")` returns
   both `LOGIN_INFO` and `SAPISID` on `.youtube.com`. It checks after every `NavigationCompleted` and on a short
   timer, then waits a few seconds for the redirect chain to settle.
4. It reads the cookies on `youtube.com` and its subdomains via `CoreWebView2.CookieManager` (deduplicated by
   domain + path + name), **closes the window** (no YouTube tab stays open, which avoids cookie rotation), deletes
   all cookies from the profile (`DeleteAllCookies()`, so the profile never holds a live copy of the session, see
   "Session longevity" below), and sends them to the engine: `PUT /api/v1/auth/cookies`. `.google.com` and
   country-domain cookies (e.g. `.google.com.tr`) are not stored: neither yt-dlp nor a browser refresh needs them,
   and a YouTube visit never rotates them (M0 spike, `docs/spikes/cookie-refresh.md`).
5. Fallback: **Import cookies.txt…** (Netscape format), via the same endpoint.

**Engine side:**
- Stores the cookies **encrypted with DPAPI** (CurrentUser) in `data\auth\cookies.bin`.
- For each job, writes a plaintext Netscape cookies file into that job's work folder, readable only by the user,
  and deletes it after the job.
- **Verification:** see below; it doubles as the keep-alive ping.

#### Session longevity (goal: the user almost never has to log in again)

Google rotates session cookies: every YouTube page visit calls `accounts.youtube.com/RotateCookies`, which replaces
`__Secure-1PSIDTS` / `__Secure-3PSIDTS`. Google accepts only the **current value and the one it replaced**; one
rotation later the older jar is dead, however young it is (M0 spike, `docs/spikes/cookie-refresh.md`). That's why
exported cookies die when a browser tab stays open. yt-dlp never rotates them.
The design therefore has **exactly one cookie lineage**, owned by the engine. Every refresher starts from the
engine's latest jar and hands its result back, and nothing else (the WebView2 profile included) keeps a copy that
could rotate.

1. **Versioned jar.** The engine stores the jar as numbered *generations* (DPAPI-encrypted) and keeps the
   current one plus the last 3 known-good ones. Every change creates a new generation through the queue actor,
   so updates are serialized. Generations older than the last browser refresh hold a dead `*PSIDTS`; they are
   kept for diagnostics, not as rollback targets.
2. **Writeback merge (every yt-dlp run), three-way.** Each job gets the current generation, and the engine
   remembers which one. After the job exits, the engine reads the cookie file yt-dlp saved and takes **only the
   cookies whose value differs from the generation the job was given**, applying their value and expiry onto the
   engine's *current* jar. A whole-file "last writer wins" would let a job that started before a browser refresh
   write the pre-refresh `*PSIDTS` back. Guards:
   - never merge after an Auth-category failure;
   - never let a writeback blank the core login cookies on `.youtube.com`: `SID`, `HSID`, `SSID`, `APISID`,
     `SAPISID`, `__Secure-1PSID`, `__Secure-3PSID`, `__Secure-1PAPISID`, `__Secure-3PAPISID`,
     `__Secure-1PSIDTS`, `__Secure-3PSIDTS`, `LOGIN_INFO`;
   - ignore removals (yt-dlp drops expired cookies);
   - keep the stored attributes (yt-dlp loses every HttpOnly flag and saves some cookies as session cookies);
   - skip `PREF` (yt-dlp rewrites it with `hl=en` / `tz=UTC`);
   - ignore cookies outside `youtube.com`.

   In practice a writeback only refreshes `SIDCC` / `__Secure-*PSIDCC` and some expiry dates. yt-dlp never
   changes a core cookie (M0 spike).
3. **Keep-alive ping (engine, every 6 h, even when idle).** The verification request (flat fetch of the
   subscriptions feed with the cookies) runs on a timer, and its writebacks are merged like a job's. The session
   stays "in use" during quiet periods, and the engine learns about expiry before a download hits it.
4. **Browser refresh (YoutubeDownloadHelper.exe, every 24 h and on demand).** Google's own page scripts rotate cookies in a way
   yt-dlp never does, so a real browser visit is the most faithful way to keep a session healthy. The UI runs
   it in the background while it's running, which is most of the time thanks to tray + autostart:
   - `POST /auth/refresh/begin` → the engine returns the current jar and **holds new cookie-using job starts**
     (lease, max 2 min). Running jobs continue with their copies.
   - Hidden WebView2 (same isolated profile): `DeleteAllCookies()`, inject the jar via `CreateCookie` +
     `AddOrUpdateCookie` (Netscape has no SameSite: Secure → `None`, else `Lax`), load `youtube.com`, wait until
     the page is idle (~20 s; `RotateCookies` returns ~2–5 s after load), export the cookies, `DeleteAllCookies()`,
     dispose the WebView2. Confirmed in the M0 spike: the page stays logged in and rotates `*PSIDTS`.
   - `PUT /auth/refresh/complete` with the new jar → the engine verifies it and keeps it only if verification
     passes. Either way it releases the lease. A rejected or lost result is safe: the stored jar is still the
     "previous" value, and the next refresh can start from it.
   - Each refresh moves the lineage, so a running job's cookie copy survives one refresh but not two. Refresh
     only on the schedule or on recovery, never in a loop.
5. **Recovery ladder before bothering the user.** When verification fails or a job hits an Auth error:
   (a) retry verification once, in case it was a transient glitch → (b) if the current generation came from a
   browser refresh, roll back to the generation that refresh started from and verify (anything older is dead)
   → (c) ask the UI (`auth.refreshRequested` event) for a browser refresh, if a UI is connected
   → (d) only then: status `Expired`, the `WaitingAuth` flow, and the "log in again" toast.
6. **Telemetry (local logs only):** each generation records its source (login / writeback / ping / browser
   refresh / rollback) and timestamps. Session lifetime and the recovery step that saved it are logged, so the
   intervals can be tuned in beta (6 h / 24 h are starting values).

Limits: Google can still end a session (password change, a security review, "sign out of all devices").
Then a re-login is unavoidable, but it stays one click.
- **Verification:** a flat fetch of `https://www.youtube.com/feed/subscriptions` with the cookies succeeds only
  when logged in. Runs on import, every 6 h (keep-alive ping), and after any Auth-category error.
- Status: `NotLoggedIn` · `Valid` (with account name if obtainable) · `Expired` · `Unknown`. When `Expired`,
  jobs run without cookies; members-only items land in `WaitingAuth`, the tray toast says "Your YouTube login
  expired — click to log in again", and a successful re-login requeues all `WaitingAuth` items. `Expired` is
  reached only after the recovery ladder below.
- "Log out" deletes the stored cookies **and** the WebView2 profile.

---

## 6. Engine API

Base: `http://127.0.0.1:<port>/api/v1`. JSON (camelCase). OpenAPI document at `/openapi/v1.json` for
non-.NET clients.

### 6.1 Discovery and security

- **Discovery file** `data\engine.json` (readable only by the user):
  `{ "apiVersion": 1, "baseUrl": "http://127.0.0.1:47815", "token": "<random 256-bit>", "pid": 1234, "engineVersion": "1.0.0" }`.
  Written atomically once the server is listening and deleted on clean shutdown. Clients treat it as stale when
  the PID is gone or the health call fails. **This file is the integration contract for other programs.**
- **Port:** the configured port (default 47815); falls back to a dynamic port if that one is taken. The actual
  port is always in the discovery file.
- **Binds to `127.0.0.1` only.** Every endpoint needs `Authorization: Bearer <token>`. Requests whose `Host`
  is not `127.0.0.1:<port>`/`localhost:<port>` are rejected (DNS-rebinding defense). Requests with an `Origin`
  header are rejected; no CORS. Together these stop web pages from driving the engine.
- The token is regenerated on each engine start.

### 6.2 Endpoints (v1)

| Method & path | Purpose |
|---|---|
| `GET /info` | Engine version, API version, tool versions, auth status, queue blocks |
| `POST /resolve` `{url}` | Metadata only: a single video, or playlist/channel entries (for the picker); flags duplicates |
| `POST /items` `{urls[] \| videoIds[], presetId?, folder?, paused?, expandPlaylists?: "all", force?}` | Add; per-URL result: added / duplicate / invalid |
| `GET /items?state=&search=&cursor=` | List/snapshot |
| `GET /items/{id}` | Detail incl. attempts and the output tail |
| `POST /items/{id}/pause` · `/resume` · `/retry` | Item control |
| `POST /items/{id}/move` `{to: "top"\|"bottom"\|"before", beforeId?}` | Reorder |
| `DELETE /items/{id}?deleteFiles=` | Remove (soft-delete: hidden from the queue and history) |
| `POST /queue/pause` · `/queue/resume` | Global control |
| `GET/POST /watchers` · `PATCH/DELETE /watchers/{id}` · `POST /watchers/{id}/check` | Watchers |
| `GET /auth` · `PUT /auth/cookies` · `DELETE /auth/cookies` · `POST /auth/verify` | Auth (status, import, log out, verify) |
| `POST /auth/refresh/begin` · `PUT /auth/refresh/complete` | Browser-refresh lease (§5.7). `begin` returns the cookie jar: any token holder can read it, which is no new exposure, since same-user processes can already decrypt DPAPI data |
| `GET /settings` · `PATCH /settings` | Settings (stored in the DB) |
| `GET /tools` · `POST /tools/check` · `POST /tools/{tool}/rollback` | Tools |
| `GET /history?search=&cursor=` | Completed and skipped items, archive |
| `GET /events` | SSE stream (below) |
| `POST /engine/shutdown` | Graceful shutdown (pause running jobs, keep partial files, flush the DB) |

### 6.3 Events (SSE)

Every event has a monotonically increasing `id` (sequence number). On reconnect, the client sends `Last-Event-ID`.
The engine replays from a ring buffer (about 2,000 events) or sends `resync`, after which the client refetches
its snapshot.

`item.added` · `item.updated` (state/metadata) · `item.progress` (throttled: phase, bytes, total, speed, eta,
fraction) · `item.removed` · `queue.reordered` · `queue.blocked`/`queue.unblocked` · `watcher.updated` ·
`watcher.checked` · `auth.changed` · `auth.refreshRequested` · `tools.changed` · `notify` (user-facing:
completed, failed, login needed, watcher found N new) · `engine.stopping` · `resync`.

`notify` events are what the UI turns into toasts, so a future UI gets the same notifications for free.

**The API is language-neutral.** User-facing information travels as `{ code, args, message }`: `code` is a stable
identifier (e.g. `error.unavailable.private`, `notify.watcher.newItems`), `args` are its parameters, and
`message` is an English fallback for third-party clients. Clients localize from `code` + `args`. Raw yt-dlp
output is passed through untranslated (Details/Log only).

---

## 7. WPF application

### 7.1 Main window (qBittorrent-style)

```
┌ Toolbar: [+ Add] [▶ Resume] [⏸ Pause] [✖ Remove] [⤒ Top] [⤓ Bottom] │ [⚙ Settings]      🔍 search ┐
├ Sidebar ─────────┬ Queue table (DataGrid, virtualized) ──────────────────────────────────────────────┤
│ STATUS           │ # │ Title │ Channel │ Status │ Progress ▓▓▓░ │ Size │ Speed │ ETA │ Preset │ Added   │
│  All (42)        │ … sortable, column chooser, widths persisted, drag-to-reorder, multi-select       │
│  Downloading (2) │                                                                                    │
│  Queued (5)      ├ Details pane (tabs): General │ Attempts & errors │ Log ─────────────────────────────┤
│  Paused · Waiting│  URL, file path, preset, availability badge (Members / Premium), timestamps…        │
│  Completed · Fail│                                                                                    │
│ WATCHERS         │                                                                                    │
│  Channel A (3 new│                                                                                    │
├──────────────────┴────────────────────────────────────────────────────────────────────────────────────┤
│ Status bar: ● Engine connected │ 👤 Logged in (or ⚠ Login expired — Log in) │ ↓ 12.3 MB/s │ yt-dlp 2026.10.x │
└───────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

- Row context menu: Open file, Open folder, Copy URL, Pause/Resume, Retry, Move to top/bottom, Remove,
  Remove and delete files.
- Queue blocks show as a dismissible banner ("Waiting for network…", "Disk full — free up space or change
  the download folder").

### 7.2 Dialogs and windows

- **Add:** multiline URL box (prefilled from the clipboard if it holds a supported URL), preset dropdown,
  destination (default: per-channel), "Add paused". Calls `/resolve`. Playlists/channels open the **picker**:
  checklist with title, duration, members/premium badge, "already downloaded" marker, select all/none.
- **Watchers:** list plus Add/Edit (URL → resolved channel name; checkboxes Videos / Shorts / Past streams /
  Members-only; preset; folder; "Also download the latest N existing videos"); Check now; enable/disable.
- **Settings:**
  - General: download folder, start with Windows, close button minimizes to tray, keep downloading after Exit.
  - Downloads: default preset, filename preview.
  - Account: status, Log in / Log out, Import cookies.txt.
  - Updates: app version + Check now; tool versions, yt-dlp channel (stable/nightly), Roll back.
  - Advanced: API port, copy API token, open data folder, open logs, **Export diagnostics** (zip of logs
    with cookies/token redacted, for helping friends remotely).
- **First-run wizard:** Welcome → download folder → install tools (progress) → YouTube login (optional;
  explains why it's needed for members-only and Premium) → start with Windows → done.

### 7.3 Tray and notifications

- Closing the window minimizes to the tray (configurable). Tray menu: Open, Pause all / Resume all, Add from
  clipboard, Exit.
- Toast library: `Microsoft.Toolkit.Uwp.Notifications` (`ToastContentBuilder`, `ToastNotificationManagerCompat`).
  It works unpackaged under Velopack and needs a Windows TFM for `YoutubeDownloadHelper.App` (`net10.0-windows10.0.19041.0`). It
  registers the Velopack AUMID with the display name "Youtube İndirgeç" and a COM activator on `current\YoutubeDownloadHelper.exe`
  (spike: `docs/spikes/velopack.md`).
- Toasts come from `notify` events: download finished (click → open folder), failed, login expired (click →
  login), watcher found new videos, app update available.

### 7.4 Engine supervision (in YoutubeDownloadHelper.exe)

- At startup: read the discovery file → `GET /info`. If it fails, start `YoutubeDownloadHelper.Engine.exe` (hidden, from the install
  folder) and wait up to 15 s for it to be ready.
- Connection lost: "Reconnecting…" banner; restart the engine with backoff (3 attempts), then an error panel with
  **Open logs** / **Export diagnostics**.
- **Exit:** `POST /engine/shutdown`, then quit. With "keep downloading after Exit" on, only the UI quits.
- If the UI crashes, the engine keeps running and the next UI launch reconnects. When "keep downloading after
  Exit" is off, the engine exits on its own after **10 minutes with no connected client and an idle queue**.
- App single instance: a second `YoutubeDownloadHelper.exe` launch activates the existing window and exits.
- Background duties while running: the 24 h browser cookie refresh and answering `auth.refreshRequested` (§5.7).

### 7.5 Localization (Turkish default, English)

- **Languages:** Turkish (`tr-TR`, default on first run regardless of the Windows language) and English
  (`en-US`). Picked in the first-run wizard's first page and in Settings → General. It switches at runtime with
  no restart.
- **Mechanism:** `.resx` resources (`Strings.resx` = Turkish as the neutral/default culture,
  `Strings.en.resx`) with generated strongly typed accessors. XAML binds through a small `Loc` markup extension
  backed by a localization service that raises change notifications, so a language switch updates the open
  windows live.
- **Everything user-facing is localized:** UI text, friendly error messages (by engine `code`), toasts, tray
  menu, wizard, and the plain-language parts of the diagnostics. Not localized: raw yt-dlp output, logs, the API.
- **Formatting:** dates, sizes ("1,5 GB" vs "1.5 GB"), speeds, and ETAs use the selected UI culture.
- **The Turkish-I rule (critical):** under `tr-TR`, `"file".ToUpper()` gives `"FİLE"` and `"FILE".ToLower()`
  gives `"fıle"`, which silently breaks comparisons. Rules: all non-display string operations (URL/host checks,
  file extensions, classifier patterns, cookie names, config keys) use `Ordinal`/`OrdinalIgnoreCase` or the
  invariant culture. Analyzers CA1304/CA1305/CA1307/CA1309/CA1310/CA1311 are set to *error*. `YoutubeDownloadHelper.Engine.exe` runs
  with `CultureInfo.InvariantCulture`. The UI culture is applied only to display.
- **Tests:** resource parity (every key exists in both languages, with matching format placeholders), and the
  Core, Engine and Client test suites run under `tr-TR` (xUnit v3 `--culture`) as well as the default culture to
  catch Turkish-I bugs. A canary test proves the culture switch took effect.
- **Paths:** the default download folder `%USERPROFILE%\Downloads\Youtube İndirgeç` contains non-ASCII
  characters on purpose, so Unicode-path problems (yt-dlp, ffmpeg, Deno) surface early. M0 spike: works with
  non-ASCII tools, work and destination folders, given `--encoding utf-8` (`docs/spikes/progress-and-paths.md`).

---

## 8. Data storage

Per-Windows-user data folder `%LOCALAPPDATA%\YoutubeDownloadHelperData\`. It must stay **outside** Velopack's
install root `%LOCALAPPDATA%\YoutubeDownloadHelper\`: Velopack's uninstall deletes the whole root, and its
updater kills every process running from it (M0 spike, `docs/spikes/velopack.md`). Kept separate, the data
survives updates, uninstall and reinstall:

```
state.db            SQLite, WAL mode, EF Core migrations (run by the engine at startup, after a DB backup)
backups\            state-<utc>-before-<migration>.db, taken only when a migration is pending (last 5 kept)
engine.json         discovery file (user-only ACL)
auth\cookies.bin    DPAPI-encrypted cookies
webview-profile\    WebView2 login profile (owned by YoutubeDownloadHelper.exe)
tools\<tool>\<ver>\ managed binaries
work\<itemId>\      partial downloads, per-job cookies file
logs\               engine-*.log, app-*.log (rolling, 14 days), jobs\<itemId>-<attempt>.log
```

Tables: `items` (id, url, extractor, video_id, title, channel, upload_date, availability, preset_id, folder,
state, position, attempt, next_attempt_at, error_category, error_message, file_path, size, timestamps, removed_at,
watcher_id?, account_id) · `item_events` (transitions; the event that ends a yt-dlp run also has its exit code
and last 200 output lines) · `watchers` · `watcher_seen` · `settings` (key → JSON) · `tools` (tool, current,
previous, channel, last_check) · `schema_info`. The download archive is a query over `items` (§5.4), not a
table.

Names are snake_case. Enums are stored by name (reordering an enum never touches data). Timestamps are UTC Unix
milliseconds, so SQLite can compare and sort them; EF Core cannot order `DateTimeOffset` stored as text.
Settings the user never changed are not stored, so they follow the current default.

---

## 9. Distribution and updates

- **Packaging:** Velopack. A single package contains `YoutubeDownloadHelper.exe`, `YoutubeDownloadHelper.Engine.exe`, and dependencies, both published
  self-contained for win-x64 into one folder (`vpk pack --mainExe YoutubeDownloadHelper.exe`). `Setup.exe` installs per user (no
  admin) to `%LOCALAPPDATA%\YoutubeDownloadHelper\` (Velopack's install root: `current\`, `packages\`,
  `Update.exe`). Pack ID `YoutubeDownloadHelper`, pack title / shortcut name "Youtube İndirgeç". The M0 spike
  confirmed the non-ASCII title in shortcuts, Add/Remove Programs and toasts (`docs/spikes/velopack.md`).
  Shortcuts point to `current\YoutubeDownloadHelper.exe` and carry the AUMID `velopack.YoutubeDownloadHelper`, which
  `VelopackApp.Run()` also sets on the process.
- **vpk and the non-ASCII title:** vpk 1.2.161 cannot build delta packages when the title is non-ASCII
  (velopack#1105, fixed upstream but only in a prerelease so far). Until a stable vpk includes the fix, pack with
  `--delta none` (full ~75 MB download per update). Keep the `Velopack` library and `vpk` on the same pinned
  version.
- **Release pipeline (GitHub Actions):** PR → build + test. Tag `v*` → publish → `vpk pack` →
  `vpk upload github` (public repo, Releases as the update feed).
- **App updates:** the UI checks at startup and every 6 h. If an update is available, a banner/toast offers
  "Restart to update". On accept: download → `POST /engine/shutdown` → **wait for the engine process to exit** →
  `ApplyUpdatesAndRestart`. The graceful stop is required: before swapping `current\`, Velopack's `Update.exe`
  hard-kills every process whose image is under the install root (no shutdown code runs). That kill is only a
  safety net. The restarted app starts the new `YoutubeDownloadHelper.Engine.exe` as usual (§7.4).
- **WebView2 runtime:** present on Windows 11 and on most Windows 10 machines. The wizard detects it and
  runs the Evergreen bootstrapper if it's missing.
- **Autostart:** `HKCU\…\Run` → `…\current\YoutubeDownloadHelper.exe --minimized` (stable across updates).
- **Uninstall:** Velopack deletes the whole install root. The `OnBeforeUninstallFastCallback` hook (killed after
  30 s, no UI; uninstall may be silent) removes the Run key and calls `ToastNotificationManagerCompat.Uninstall()`.
  The data folder (§8) is outside the install root and is **kept**, so a reinstall picks it up.
- **Code signing:** none in v1. The friends get a short "More info → Run anyway" note for SmartScreen. Azure
  Trusted Signing is an option later.

---

## 10. Logging and diagnostics

- Serilog, structured, rolling files for both processes; correlation by item ID and attempt.
- Cookie values, the API token, and the `Authorization` header are **always redacted**.
- **Export diagnostics** collects logs, the anonymized settings, tool versions, and recent `item_events`.

## 11. Testing strategy

- **Unit:** state machine transitions (table-driven), error classifier against captured stderr fixtures, retry
  scheduling with a fake clock, preset → argument snapshots, watcher diffing, progress parsing, cookie merge
  guards and the recovery ladder, resource parity. Core, Engine and Client tests also run under `tr-TR`.
- **Integration:** the real engine via `TestServer` with `YoutubeDownloadHelper.FakeYtDlp` scenarios: progress, pause/resume,
  crash recovery (kill mid-download, restart, resumes), auth → WaitingAuth → re-login, 429 backoff, tool broken →
  update → requeue, disk full block, SSE reconnect/resync.
- **Contract:** OpenAPI snapshot test, so API changes are deliberate.
- **Architecture:** dependency rules (see 4.1).
- **Manual release checklist (real YouTube):** public video, members-only video, Premium format, playlist
  picker, watcher with members tab, sleep/resume mid-download, network unplugged, update with downloads running.

---

## 12. Milestones

| # | Milestone | Done when |
|---|---|---|
| **M0** | **Spikes** (de-risk before building) | (a) Google sign-in works in WebView2 and the exported cookies download a members-only video; (b) progress-template JSON parsing; (c) cookies + Deno are enough without a PO-token plugin; (d) a Velopack package with two executables updates cleanly while the engine is running, with a non-ASCII title; (e) cookies **injected** into a fresh WebView2 profile keep the session logged in (needed for browser refresh), and yt-dlp writes rotated cookies back; (f) yt-dlp + ffmpeg + Deno work with a non-ASCII download path |
| M1 | Engine core | Solution skeleton, persistence, queue actor + state machine, runner, classifier/retry, archive, tool install, API + SSE + OpenAPI. **Usable headless via curl.** |
| M2 | WPF client | Client library, localization infrastructure (tr/en, from the first window), main window, Add dialog + playlist picker, details pane, tray, toasts, engine supervision |
| M3 | Auth | Login window, cookies.txt import, verification, versioned jar + writeback merge, keep-alive ping, browser refresh lease, recovery ladder, WaitingAuth flow, expiry toasts |
| M4 | Watchers | Model, polling, members tab, diffing/backfill, UI |
| M5 | Updates & distribution | Tool auto-update + rollback, Velopack app updates, first-run wizard, CI release pipeline, diagnostics export |
| M6 | Hardening & friend beta | Soak test (long queue, sleep/resume, network loss), wording pass on all errors, beta with friends |
| v2 | Later | Livestreams, subtitles, SponsorBlock, speed limits/schedule, concurrency UI, `ydh` CLI, browser extension, LAN access, clipboard monitoring, drag & drop, multiple accounts |

## 13. Risks

| Risk | Likelihood / impact | Mitigation |
|---|---|---|
| Google blocks sign-in in embedded WebView2 ("browser may not be secure") | Low / High (M0 spike: sign-in works with the default user agent, `docs/spikes/webview2-login.md`) | Fallbacks if Google starts blocking it: cookies.txt import (always available); optional `--cookies-from-browser firefox` import. |
| YouTube changes break yt-dlp | High / Medium | Auto-update, forced update on extractor errors, nightly channel, rollback |
| Bot checks / PO-token requirements tighten | Medium / High | Cookies + Deno by default; tool manager can add the plugin; classifier routes these errors to Auth/ToolBroken |
| Account flagged for automated downloading | Low / High | Concurrency 2, start jitter, watcher interval ≥30 min, staggered polls |
| Cookies invalidated by rotation | Medium / Medium | Single engine-owned cookie lineage; three-way writeback merge; keep-alive pings; browser refresh; one-step rollback; one-click re-login as the last resort |
| A second rotator moves the session two steps past the engine's jar (M0 spike: Google accepts only the current and previous `*PSIDTS`; yt-dlp never rotates) | Medium / High | Browser refresh starts from the engine's current jar under a lease; the WebView2 profile's cookies are deleted after every login/refresh, so nothing else holds a live copy; only verified results are kept |
| Turkish-I / culture bugs | Medium / Medium | Ordinal/invariant rules enforced by analyzers; Core/Engine/Client tests under `tr-TR`; engine runs invariant |
| Antivirus quarantines yt-dlp.exe (PyInstaller build) | Medium / Medium | Health check → `ToolsMissing` → reinstall; a note for the friends |
| Long or Unicode paths, odd titles | Medium / Low | yt-dlp Windows filename sanitizing + title trimming; tests with real titles |
| Local malware calls the API | Low / Medium | Token, user-only ACLs; same-user malware is out of scope (it could read browser cookies anyway) |

## 14. Open questions

None blocking. To be tuned during beta: keep-alive ping and browser-refresh intervals (6 h / 24 h starting values).
How long an unused jar stays valid over days is measured in the soak test (#65; questions in
`docs/spikes/cookie-refresh.md`, "Multi-day check").

Resolved 2026-10-27: app name, yt-dlp channel, "Best" preset, session longevity, live URL handling, UI language.
See the decisions log (§3).
