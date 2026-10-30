# YoutubeDownloadHelper.FakeYtDlp

A stand-in for `yt-dlp.exe` used by the integration tests and for UI development (PLAN.md §4.2, §11). It accepts
the command line the engine builds, picks a **scenario from the URL**, and writes output in the format real
yt-dlp produces (spike #33, `docs/spikes/progress-and-paths.md`). It never touches the network.

Point the engine at `YoutubeDownloadHelper.FakeYtDlp.exe` instead of `yt-dlp.exe` and queue `https://fake.test/…`
URLs. Test projects get the exe in their output folder with a `ProjectReference` (see
`YoutubeDownloadHelper.Core.Tests`).

## URLs

```
https://fake.test/<scenario>[/<sub>]?<param>=<value>&…
```

Real URLs work too: `https://www.youtube.com/watch?v=<id>` and `https://youtu.be/<id>` run `success` with that id,
and `https://www.youtube.com/feed/subscriptions` runs `subscriptions`. Anything else fails with
`ERROR: Unsupported URL: …`, exit code 1.

## Scenarios

Exit code 0 unless noted. "Login" means the `--cookies` file has a non-empty `LOGIN_INFO` cookie.

### Downloads

| Scenario | Behaviour |
|---|---|
| `success` | Two formats (`399` video + `140` audio), `Merger`, then `Metadata` / `EmbedThumbnail` (if `--embed-*` is passed) and `MoveFiles`, then `after_move`. With `-x`: one format (`251`), `ExtractAudio` to `--audio-format`. If the destination already exists: no download, no merge, the other post-processors still run (spike #33). |
| `slow` | `success` throttled: default `size=2M`, `speed=256K` (≈ 8 s). |
| `hang` | Downloads to `stop` (default 0.5 of the total), then never exits. |
| `spawn-child` | Starts a child process (prints `[fake] child pid <pid>` to stdout, even when quiet; it shares the fake's stdout/stderr like ffmpeg does), then behaves like `hang`. For tree-kill tests. |
| `resume` | With no `.part` / format file in the temp folder: downloads to `stop` (default 0.5) and hangs, so the test kills it. Otherwise it resumes from the `.part` length and completes. (Every download scenario resumes from an existing `.part`; this one also hangs on a fresh start.) |
| `cookie-writeback` | `success`, and the saved cookie file has `-rotated` appended to the cookies named in `rotate`. |
| `live` | `-J`: `live_status` `is_live`. Download: fragment progress (no total) every 250 ms, never exits. |

### Failures (exit code 1)

| Scenario | When | Last stderr line |
|---|---|---|
| `http-429` | after `plan` | `ERROR: unable to download video data: HTTP Error 429: Too Many Requests` |
| `http-5xx` | after `plan` | same with `status` (default 503; 500, 502, 504 have their reason phrases) |
| `connection-reset` | at `stop` (default 0.25) | `ERROR: [download] Got error: ('Connection aborted.', ConnectionResetError(10054, …)). Giving up after 10 retries` (the 10 retry notices go to stdout when not quiet) |
| `disk-full` | at `stop` (default 0.5) | `ERROR: unable to write data: [Errno 28] No space left on device` |
| `access-denied` | after `plan` | `ERROR: unable to open for writing: [Errno 13] Permission denied: '<.part path>'` |
| `members-only` | extraction, unless logged in | `ERROR: [youtube] <id>: Join this channel to get access to members-only content like this video, and other exclusive perks.` |
| `premium-only` | extraction, unless logged in | `ERROR: [youtube] <id>: This video is only available to Music Premium members` |
| `age-restricted` | extraction, unless logged in | `ERROR: [youtube] <id>: Sign in to confirm your age. This video may be inappropriate for some users. Use --cookies-from-browser or --cookies …` |
| `bot-check` | extraction, always | `ERROR: [youtube] <id>: Sign in to confirm you’re not a bot. Use --cookies-from-browser or --cookies …` (curly apostrophe) |
| `private` | extraction | `ERROR: [youtube] <id>: Private video. Sign in if you've been granted access to this video. Use --cookies-from-browser …` |
| `removed` | extraction | `ERROR: [youtube] <id>: Video unavailable. This video has been removed by the uploader` |
| `geo-blocked` | extraction | `ERROR: [youtube] <id>: The uploader has not made this video available in your country. You might want to use a VPN or a proxy server (with --proxy) to workaround.` |
| `extractor-failure` | extraction | `ERROR: [youtube] <id>: Unable to extract initial player response; please report this issue on  https://github.com/yt-dlp/yt-dlp/issues?q= , …` |
| `nsig` | extraction | `WARNING: … nsig extraction failed: Some formats may be missing`, the `n = … ; player = …` line, then `ERROR: [youtube] <id>: Requested format is not available. Use --list-formats for a list of available formats` |
| `unknown-error` | extraction | `ERROR: [youtube] <id>: Something went wrong in the fake (unknown error)` (matches no classifier pattern) |
| `upcoming` | extraction | `ERROR: [youtube] <id>: This live event will begin in a few moments.` With `-J --ignore-no-formats-error`: metadata with `live_status` `is_upcoming`, exit 0. |

The exact strings are in `YtDlpMessages.cs`. The auth hint (`Use --cookies-from-browser or --cookies for the
authentication. See  https://…`) is appended where yt-dlp appends it.

### Metadata and listings (`-J`)

| URL | Output |
|---|---|
| any video scenario with `-J` | One JSON object (`_type` `video`): `availability` is `public`, or `subscriber_only` (`members-only`), `premium_only` (`premium-only`), `needs_auth` (`age-restricted`); `live_status` is `not_live`, `is_live` (`live`) or `is_upcoming` (`upcoming`). Scenarios that fail during extraction fail under `-J` too; download-phase failures don't. |
| `playlist` | `--flat-playlist -J` listing, `playlist_count` = `count`. |
| `channel/videos`, `channel/shorts`, `channel/streams`, `channel/members` | Channel tab listings. `streams` entries have `live_status` `was_live`; `members` entries have `availability` `subscriber_only` and need login (otherwise `ERROR: [youtube:tab] UUMO…: The playlist does not exist.`). `channel` alone is `videos`. |
| `subscriptions` | The auth verification. Logged in: a listing. Logged out: `ERROR: [youtube:tab] Login details are needed to download this content. Use --cookies-from-browser …`, exit 1. |

Listings are numbered newest first, `count` down to 1, so a later poll with a higher `count` has new entries at the
top. `--playlist-end N` keeps the first N. Ids are 11 characters (`fkv00000012`: `fk` + tab letter + number);
entry URLs are `https://fake.test/<entry>?id=<id>`, so queuing an entry runs the `entry` scenario. Without `-J`,
listings fail.

## URL parameters

| Parameter | Default | Used by |
|---|---|---|
| `id` | `fk` + scenario letters, padded to 11 (`fksuccess00`) | video scenarios |
| `title`, `channel` | `Fake video (<scenario>)`, `Fake Channel` | video scenarios, listings |
| `upload_date` | `20260102` | video scenarios |
| `size` | `192K` (`2M` for `slow`) | total bytes; ¾ video, ¼ audio. Suffixes `K`, `M`, `G` (binary). Files are created with `SetLength`, so large sizes are cheap. |
| `speed` | unthrottled (reported as 10 MiB/s) | bytes per second; when set, the fake sleeps and writes 4 progress lines per second |
| `stop` | see the scenario | fraction of `size` where `hang`, `spawn-child`, `resume`, `connection-reset`, `disk-full` stop |
| `status` | `503` | `http-5xx` |
| `rotate` | `SIDCC,__Secure-1PSIDCC,__Secure-3PSIDCC` | `cookie-writeback` |
| `count`, `prefix`, `entry` | `5`, `fk<tab letter>`, `success` | listings |

## Command line

Understood: `--version` (prints `2026.10.26`, or `FAKE_YTDLP_VERSION` if set), `-J` / `-j`, `--flat-playlist`,
`--playlist-end`, `--no-playlist`, `--ignore-no-formats-error`, `-q`, `--progress`, `--no-progress`,
`--progress-template`, `--print` / `-O`, `-P` (`temp:` / `home:` / plain), `-o`, `--cookies`, `-x`,
`--audio-format`, `--embed-metadata`, `--embed-chapters`, `--embed-thumbnail`, `--encoding`. Everything else is
ignored; options known to take values (`--ffmpeg-location`, `--js-runtimes`, `--replace-in-metadata` with 3, …)
consume them. No URL: the yt-dlp usage error, exit code 2.

Behaviour copied from yt-dlp:

- **Quiet and progress:** `--print` and `-J` imply `--quiet`; a quiet run prints progress only with `--progress`.
  Without quiet, the `[youtube]` / `[info]` / `[download]` text lines go to stdout.
- **Templates:** the fake does not evaluate yt-dlp's template language for progress. Any `download:` template
  produces the spike's `YDH1 {"k":"dl",…}` line (stdout); any `postprocess:` template the `pp` line (**stderr**). A
  `--print` template containing `"k":"plan"` or `"k":"moved"` produces that line; any other `--print` value is a
  field name (`filepath`, `title`, …) or an output template. Supported `-o` / `--print` syntax: `%(field)s`,
  `%(field)d`, `%(upload_date>%Y-%m-%d)s`, `%%`; unknown fields are `NA`; field values in filenames get yt-dlp's
  full-width replacements for `\/:*?"<>|`.
- **JSON** is written like Python's `json.dumps`: `", "` / `": "` separators, non-ASCII as lowercase `\uXXXX`,
  missing values omitted, floats with a fraction (`300.0`).
- **Encoding:** with `--encoding utf-8`, UTF-8. Without it, cp1252 with unsupported characters (`İ`, `ı`, emoji)
  silently dropped, as `yt-dlp.exe` does (spike #33). Lines end with `\n` on both streams.
- **Files:** formats download to `<temp>/<name>.f<format>.<ext>.part`, are renamed when complete, merged into
  `<temp>/<name>.<ext>`, then moved to `<home>`. The work folder is left without files.
- **Cookies:** the `--cookies` file is saved back on every exit that isn't a kill, with a yt-dlp header and every
  `#HttpOnly_` prefix dropped (PLAN.md §5.7).

## Where the error text comes from

The spike captured only successful runs and one path error, so these strings are not from captures. They come
from yt-dlp's source (`report_error` / `ExtractorError` messages in `YoutubeDL.py`, `downloader/http.py`,
`extractor/youtube`) and YouTube's playability messages as yt-dlp prints them. The least certain ones are
`premium-only`, the `nsig` warning (newer yt-dlp versions word JS challenge failures differently), the
`connection-reset` retry text and the `UUMO` members-tab error. When a real capture disagrees, update
`YtDlpMessages.cs` and the classifier fixtures together.
