# Youtube İndirgeç — Design Plan

Status: **Draft v1** · Created 2026-10-27 · Supersedes the prototype in `.archive/` (not used as a reference)

Naming: **"Youtube İndirgeç"** is the only user-facing name (window titles, shortcuts, installer, toasts, tray).
Internally (namespaces, data folder, Velopack pack ID, repo) we use `YoutubeDownloadHelper` /
`youtube-download-helper` / `Ydh`. Keep the display name in one constant/resource; never derive paths or IDs
from it (it contains non-ASCII characters).

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
| Name | User-facing "Youtube İndirgeç"; internal `YoutubeDownloadHelper` / `Ydh`. |
| Stack | C# / .NET 10, WPF with the built-in Fluent theme (follows system light/dark). Windows 10 + 11, x64. |
| Process model | **Separate headless engine process** (`Engine.exe`) + WPF client (`App.exe`). |
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
| Session longevity | **Keep the login alive as long as possible; re-login is the last resort.** Engine owns a single cookie lineage: yt-dlp writebacks merged in, periodic keep-alive pings, periodic hidden-browser refresh, last-known-good rollback (§5.7). |
| UI language | Turkish (default) and English from day one; switchable in Settings. The engine API is language-neutral (codes + args) (§7.5). |
| "Best" preset | yt-dlp's default format selection and container choice (simplest). |
| Plan location | This file. |

---

## 4. Architecture

### 4.1 Process model

```
┌──────────── App.exe (WPF) ────────────┐        ┌──────────────── Engine.exe (headless) ────────────────┐
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
src/
  Ydh.Contracts/   DTOs, enums, event types, API route constants. No dependencies. Shared by engine + .NET clients.
  Ydh.Core/        Engine logic: queue actor, state machine, scheduler, runner, classifier, watchers, tools,
                   auth store, persistence (EF Core + SQLite). No ASP.NET, no UI.
  Ydh.Engine/      Executable host: generic host, Kestrel minimal-API endpoints → Core services, SSE,
                   discovery file, single-instance mutex, logging.
  Ydh.Client/      Typed .NET API client (HttpClient + SSE reader, reconnect/resync logic).
  Ydh.App/         WPF app (CommunityToolkit.Mvvm): windows, view models, tray, toasts, WebView2 login,
                   engine supervisor, Velopack integration.
tests/
  Ydh.Core.Tests/        Unit tests: state machine, classifier, retry policy, argument builder, watcher diffing.
  Ydh.Engine.Tests/      API integration tests via ASP.NET TestServer + fake yt-dlp.
  Ydh.App.Tests/         View-model tests against a fake client.
  Ydh.ArchTests/         Dependency-rule tests (App ↛ Core, Core ↛ ASP.NET, etc.).
tools/
  Ydh.FakeYtDlp/   Console app that mimics yt-dlp output/exit codes per scenario (progress, auth error, 429,
                   extractor failure, disk full, slow, hang). Used by integration tests and for UI development.
```

Build conventions: central package management, nullable enabled, warnings as errors, `Directory.Build.props`.

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
 Removed (soft-deleted; stays in history)
 Reserved for v2: WaitingForStream, Recording
```

- **Resolving:** `yt-dlp -J --no-playlist` fetches metadata: title, channel, upload date, duration, availability
  (`public` / `subscriber_only` / `premium_only` / `needs_auth`), `live_status`, size estimate, and the final filename.
  `live_status` is stored and shown as a badge, but **does not change behavior in v1**. A live URL is downloaded
  however yt-dlp handles it (records from "now" until the stream ends); an upcoming one fails with yt-dlp's own
  message, run through the normal classifier. Known v1 limitation: pausing a live recording cannot resume it.
- **Pause:** kills the yt-dlp process tree and keeps the partial files. **Resume** reruns yt-dlp, which continues the download.
- **Crash recovery:** at engine start, items in `Resolving`/`Downloading`/`PostProcessing` go back to `Queued`
  (marked "interrupted") and resume from their partial files. Orphaned work folders with no matching item are cleaned up.
- Every transition is written to `item_events` (timestamp, from, to, reason, error category, attempt), which
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
| Best quality (default) | yt-dlp's default format selection and container (no custom sort): highest quality available, including Premium formats when logged in; output may be MKV/WebM |
| 1080p | ≤1080p, prefers MP4/H.264 for compatibility with any player |
| 720p | ≤720p, MP4 |
| Audio (M4A) | Best audio, no re-encode |
| Audio (MP3) | Best audio converted to MP3 |

The exact format strings are decided in M1 and covered by argument-builder snapshot tests.

### 5.4 yt-dlp runner

- Every invocation runs inside a **Windows Job Object** (`KILL_ON_JOB_CLOSE`), so yt-dlp and its ffmpeg children
  die with the engine. Cancel or pause kills the whole tree.
- Fixed arguments on every run: `--ignore-config` (the user's global yt-dlp config can never interfere),
  `--no-playlist` for single items, `--newline`, a machine-readable `--progress-template` (JSON with a prefix), and
  `--print after_move:filepath`. Also a private cache directory, explicit `--ffmpeg-location`, an explicit JS
  runtime pointing at the managed Deno, `--embed-metadata --embed-thumbnail --embed-chapters`, and UTF-8 output.
- **Paths:** temporary/partial files go to `data\work\<itemId>\` (`-P temp:`); the finished file moves to the
  destination. If the destination filename already exists, the engine adds ` [<videoId>]` to the output template.
- Progress lines are parsed and coalesced into **at most 4 updates per second per item**. Progress is kept in
  memory and checkpointed to the DB at most every 10 s.
- The last 200 lines of output are kept per attempt (DB) for the details pane; full logs go to `logs\jobs\`.
- **Our own archive** in SQLite (key: `extractor + video id`, matching yt-dlp's archive format) replaces
  `--download-archive`, so the archive is queryable and shared with watchers and the Add dialog.

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
1. The user clicks **Log in to YouTube** (wizard or Settings → Account). `App.exe` opens a WPF window with
   WebView2, using a **dedicated user-data folder** (`data\webview-profile\`, separate from the user's real browser).
2. It navigates to the Google sign-in page with `continue=https://www.youtube.com`. The user signs in normally
   (2FA, passkeys, etc. are handled by Google's own pages). Google may insert interstitials (e.g. "add recovery
   options" on `gds.google.com`), so the window must not assume a fixed page sequence. The default WebView2 user
   agent works; no override is needed (M0 spike, `docs/spikes/webview2-login.md`).
3. The app detects that login finished when `CookieManager.GetCookiesAsync("https://www.youtube.com/")` returns
   both `LOGIN_INFO` and `SAPISID` on `.youtube.com`. It checks after every `NavigationCompleted` and on a short
   timer, then waits a few seconds for the redirect chain to settle.
4. It reads all cookies for `youtube.com` / `google.com` via `CoreWebView2.CookieManager`, **closes the window**
   (no YouTube tab stays open, which avoids cookie rotation), and sends them to the engine:
   `PUT /api/v1/auth/cookies`. Sign-in also sets cookies on the account's country Google domain (e.g.
   `.google.com.tr`); yt-dlp doesn't need them, and spike #35 decides whether a browser refresh does.
   yt-dlp itself needs only the core `.youtube.com` cookies (the merge-guard list in "Session longevity" (2) below).
5. Fallback: **Import cookies.txt…** (Netscape format), via the same endpoint.

**Engine side:**
- Stores the cookies **encrypted with DPAPI** (CurrentUser) in `data\auth\cookies.bin`.
- For each job, writes a plaintext Netscape cookies file into that job's work folder, readable only by the user,
  and deletes it after the job.
- **Verification:** see below; it doubles as the keep-alive ping.

#### Session longevity (goal: the user almost never has to log in again)

Google rotates session cookies (e.g. `__Secure-*PSIDTS`). When **two clients share one session and rotate it
independently**, each one invalidates the other. That's why exported cookies die when a browser tab stays open.
The design therefore has **exactly one cookie lineage**, owned by the engine. Every refresher starts from the
engine's latest jar and hands its result back.

1. **Versioned jar.** The engine stores the jar as numbered *generations* (DPAPI-encrypted) and keeps the
   current one plus the last 3 known-good ones. Every change creates a new generation through the queue actor,
   so updates are serialized.
2. **Writeback merge (every yt-dlp run).** Each job gets the current generation. After it exits, the engine reads
   the cookie file yt-dlp saved and merges it **per cookie, last writer wins**. Guards: never merge after an
   Auth-category failure; never let a writeback delete or blank the core login cookies (`SID`, `HSID`, `SSID`,
   `APISID`, `SAPISID`, `__Secure-*PSID*`, `LOGIN_INFO`); ignore cookies for unrelated domains.
3. **Keep-alive ping (engine, every 6 h, even when idle).** The verification request (flat fetch of the
   subscriptions feed with the cookies) runs on a timer, and its writebacks are merged like a job's. The session
   stays "in use" during quiet periods, and the engine learns about expiry before a download hits it.
4. **Browser refresh (App.exe, every 24 h and on demand).** Google's own page scripts rotate cookies in a way
   yt-dlp never does, so a real browser visit is the most faithful way to keep a session healthy. The UI runs
   it in the background while it's running, which is most of the time thanks to tray + autostart:
   - `POST /auth/refresh/begin` → the engine returns the current jar and **holds new cookie-using job starts**
     (lease, max 2 min). Running jobs continue with their copies.
   - Hidden WebView2 (same isolated profile): inject the jar via `CookieManager.AddOrUpdateCookie`, load
     `youtube.com`, wait until the page is idle (~20 s), export the cookies, dispose the WebView2.
   - `PUT /auth/refresh/complete` with the new jar → the engine verifies it and keeps it only if verification
     passes. Either way it releases the lease.
5. **Recovery ladder before bothering the user.** When verification fails or a job hits an Auth error:
   (a) retry verification once, in case it was a transient glitch → (b) roll back to the previous known-good
   generation and verify → (c) ask the UI (`auth.refreshRequested` event) for a browser refresh, if a UI is connected
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
| `DELETE /items/{id}?deleteFiles=` | Remove (soft-delete to history) |
| `POST /queue/pause` · `/queue/resume` | Global control |
| `GET/POST /watchers` · `PATCH/DELETE /watchers/{id}` · `POST /watchers/{id}/check` | Watchers |
| `GET /auth` · `PUT /auth/cookies` · `DELETE /auth/cookies` · `POST /auth/verify` | Auth (status, import, log out, verify) |
| `POST /auth/refresh/begin` · `PUT /auth/refresh/complete` | Browser-refresh lease (§5.7). `begin` returns the cookie jar: any token holder can read it, which is no new exposure, since same-user processes can already decrypt DPAPI data |
| `GET /settings` · `PATCH /settings` | Settings (stored in the DB) |
| `GET /tools` · `POST /tools/check` · `POST /tools/{tool}/rollback` | Tools |
| `GET /history?search=&cursor=` | Completed/removed items, archive |
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
- Toasts come from `notify` events: download finished (click → open folder), failed, login expired (click →
  login), watcher found new videos, app update available.

### 7.4 Engine supervision (in App.exe)

- At startup: read the discovery file → `GET /info`. If it fails, start `Engine.exe` (hidden, from the install
  folder) and wait up to 15 s for it to be ready.
- Connection lost: "Reconnecting…" banner; restart the engine with backoff (3 attempts), then an error panel with
  **Open logs** / **Export diagnostics**.
- **Exit:** `POST /engine/shutdown`, then quit. With "keep downloading after Exit" on, only the UI quits.
- If the UI crashes, the engine keeps running and the next UI launch reconnects. When "keep downloading after
  Exit" is off, the engine exits on its own after **10 minutes with no connected client and an idle queue**.
- App single instance: a second `App.exe` launch activates the existing window and exits.
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
  invariant culture. Analyzers CA1304/CA1305/CA1309/CA1310/CA1311 are set to *error*. `Engine.exe` runs
  with `CultureInfo.InvariantCulture`. The UI culture is applied only to display.
- **Tests:** resource parity (every key exists in both languages, with matching format placeholders), and the
  Core test suite runs under `tr-TR` as well as invariant culture to catch Turkish-I bugs.
- **Paths:** the default download folder `%USERPROFILE%\Downloads\Youtube İndirgeç` contains non-ASCII
  characters on purpose, so Unicode-path problems (yt-dlp, ffmpeg, Deno) surface early. The M0 spike checks it.

---

## 8. Data storage

Per-Windows-user data folder `%LOCALAPPDATA%\YoutubeDownloadHelper\`, separate from Velopack's install folder,
so it survives reinstalls and updates:

```
state.db            SQLite, WAL mode, EF Core migrations (run by the engine at startup, after a DB backup)
engine.json         discovery file (user-only ACL)
auth\cookies.bin    DPAPI-encrypted cookies
webview-profile\    WebView2 login profile (owned by App.exe)
tools\<tool>\<ver>\ managed binaries
work\<itemId>\      partial downloads, per-job cookies file
logs\               engine-*.log, app-*.log (rolling, 14 days), jobs\<itemId>-<attempt>.log
```

Tables: `items` (id, url, extractor, video_id, title, channel, upload_date, availability, preset_id, folder,
state, position, attempt, next_attempt_at, error_category, error_message, file_path, size, timestamps,
watcher_id?, account_id) · `item_events` · `archive` (extractor, video_id, item_id, file_path, downloaded_at) ·
`watchers` · `watcher_seen` · `settings` (key → JSON) · `tools` (tool, current, previous, channel, last_check) ·
`schema_info`.

---

## 9. Distribution and updates

- **Packaging:** Velopack. A single package contains `App.exe`, `Engine.exe`, and dependencies, published
  self-contained for win-x64. `Setup.exe` installs per user (no admin). Pack ID `YoutubeDownloadHelper`, pack
  title / shortcut name "Youtube İndirgeç" (verify in M0 that non-ASCII titles work in shortcuts and toasts).
- **Release pipeline (GitHub Actions):** PR → build + test. Tag `v*` → publish → `vpk pack` →
  `vpk upload github` (public repo, Releases as the update feed).
- **App updates:** the UI checks at startup and every 6 h. If an update is available, a banner/toast offers
  "Restart to update". On accept: download → `POST /engine/shutdown` → apply and restart.
- **WebView2 runtime:** present on Windows 11 and on most Windows 10 machines. The wizard detects it and
  runs the Evergreen bootstrapper if it's missing.
- **Autostart:** `HKCU\…\Run` → `App.exe --minimized`. Velopack uninstall hook removes it and asks whether to
  delete the data folder.
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
  guards and the recovery ladder, resource parity. Core tests also run under the `tr-TR` culture.
- **Integration:** the real engine via `TestServer` with `Ydh.FakeYtDlp` scenarios: progress, pause/resume,
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
| Cookies invalidated by rotation | Medium / Medium | Single engine-owned cookie lineage; writeback merge; keep-alive pings; browser refresh; last-known-good rollback; one-click re-login as the last resort |
| Two refreshers fork the session (browser + yt-dlp rotate independently) | Medium / High | Browser refresh starts from the engine's current jar and holds new cookie-using job starts under a lease; only verified results are kept |
| Turkish-I / culture bugs | Medium / Medium | Ordinal/invariant rules enforced by analyzers; Core tests under `tr-TR`; engine runs invariant |
| Antivirus quarantines yt-dlp.exe (PyInstaller build) | Medium / Medium | Health check → `ToolsMissing` → reinstall; a note for the friends |
| Long or Unicode paths, odd titles | Medium / Low | yt-dlp Windows filename sanitizing + title trimming; tests with real titles |
| Local malware calls the API | Low / Medium | Token, user-only ACLs; same-user malware is out of scope (it could read browser cookies anyway) |

## 14. Open questions

None blocking. To be tuned during beta: keep-alive ping and browser-refresh intervals (6 h / 24 h starting values).

Resolved 2026-10-27: app name, yt-dlp channel, "Best" preset, session longevity, live URL handling, UI language.
See the decisions log (§3).
