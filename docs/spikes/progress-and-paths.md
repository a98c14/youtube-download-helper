# Spike: yt-dlp progress-template JSON and non-ASCII download paths

Issue #33 · run 2026-10-28 · harness: `spikes/progress-and-paths/` (throwaway, not part of the solution)

Plan references: §5.4 (yt-dlp runner), §7.5 (non-ASCII default folder), §8 (data folder), §12 M0 (b) and (f).

## Result

| Question | Answer |
|---|---|
| Machine-readable progress for download **and** post-processing? | **Yes.** Three `YDH1 ` + JSON line kinds from `--progress-template` / `--print`, plus a fourth (`plan`) before the download. See [Templates](#templates). |
| Can the JSON be broken by the console encoding? | **No.** `%(…)j` is `json.dumps` with ASCII escaping: every `YDH1` line is pure ASCII (`\u0130`, `\ud83d\ude00`), whatever the encoding. |
| Where do the lines go? | Download progress, `plan` and `moved` go to **stdout**. Post-processing progress goes to **stderr**, together with `WARNING:` / `ERROR:`. The runner must parse **both** streams. |
| Is `--print` + progress OK? | Only with `--progress`. `--print` implies `--quiet`, and without `--progress` there are **no** progress lines at all (verified). |
| Non-ASCII tools folder, work folder (`-P temp:`), destination (`-P home:`), title, channel subfolder? | **Works** for yt-dlp, Deno and ffmpeg/ffprobe: `İ`, `ı`, `ç`, emoji, CJK in every path at once, for merge, audio extraction and HLS. Downloaded, merged, embedded and moved, and the work folder is left without files. |
| UTF-8 output | **`--encoding utf-8` is required, and it is all that's needed.** Without it the frozen `yt-dlp.exe` writes **cp1252** and silently **drops** characters cp1252 lacks (`İ`, `ı`, emoji, CJK; `ç` survives), on stdout **and** stderr. `PYTHONUTF8=1` and `PYTHONIOENCODING=utf-8` have **no effect** on `yt-dlp.exe`. |
| `--embed-metadata --embed-thumbnail --embed-chapters` on those paths | **Works.** ffprobe shows the non-ASCII title, the artist, all 3 chapters and the cover in MKV, MP4 and MP3. |
| JS runtime flag | `--js-runtimes deno:<path>` (path to `deno.exe` or its folder). Deno is the only runtime enabled by default, so this only pins the path. `--remote-components` isn't needed: the official exe bundles `yt_dlp_ejs` 0.8.0. |
| ffmpeg flag | `--ffmpeg-location <path>` (path to `ffmpeg.exe` or its folder; ffprobe is found next to it). |

## Environment

| | |
|---|---|
| OS | Windows 11 Pro 10.0.26200, x64, ANSI code page 1252, OEM 437, `en-US` |
| yt-dlp | 2026.10.26 stable, `yt-dlp.exe` (`win_exe`), SHA-256 checked against `SHA2-256SUMS` |
| Deno | 2.9.8, `deno-x86_64-pc-windows-msvc.zip`, SHA-256 checked |
| ffmpeg / ffprobe | yt-dlp's FFmpeg-Builds `ffmpeg-master-latest-win64-gpl.zip` (N-128129-g58584087cf-20261027), SHA-256 checked |
| Videos | `jNQXAC9IVRw` ("Me at the zoo", 19 s, 3 chapters) for merge and audio; `b1Fo_M_tj6w` (YouTube Creators, 79 s, 3 chapters) for HLS |

The user environment on this machine has `PYTHONIOENCODING=utf-8:surrogateescape` set globally. The harness
removes every `PYTHON*` variable from the child environment, so no result relies on it.

## Harness

`spikes/progress-and-paths/run.ps1` starts yt-dlp the way `Engine.exe` will: `ProcessStartInfo.ArgumentList` (no
hand-built quoting), a controlled environment, and stdout/stderr copied as **raw bytes** to files.
`analyze.ps1` checks each capture: ASCII/UTF-8 validity, line endings, line kinds, and the post-processor
sequence.

Every run uses non-ASCII paths everywhere. A stand-in for a Windows profile with a Turkish user name holds the
plan's data folder (§8) and the default download folder (§7.5):

```
work\Kullanıcı İ 😀 用户\
  AppData\Local\YoutubeDownloadHelperData\
    tools\{yt-dlp,deno,ffmpeg}\      ← the tools run from here
    work\<itemId>\                    ← -P temp:
    cache\yt-dlp\                     ← --cache-dir
  Downloads\Youtube İndirgeç\<case>\  ← -P home:
```

`--replace-in-metadata title "^" "İzmir ılık 😀 東京 — "` adds non-ASCII text to the title, so it reaches the
filename **and** the embedded tags. The output template is the plan's
`<Channel>\<YYYY-MM-DD> - <Title>.<ext>`.

## Templates

Prefix: **`YDH1 `** (with the trailing space). `1` is a version: if the fields ever change incompatibly, bump
it. After the prefix, each line is one JSON object with a kind field `k`.

Exact argument values (one `ArgumentList` entry each):

```
--progress-template
download:YDH1 {"k":"dl","p":%(progress.{status,downloaded_bytes,total_bytes,total_bytes_estimate,speed,eta,elapsed,fragment_index,fragment_count})j,"i":%(info.{id,format_id})j}
--progress-template
postprocess:YDH1 {"k":"pp","p":%(progress.{status,postprocessor})j,"i":%(info.{id,filepath})j}
--print
before_dl:YDH1 {"k":"plan","i":%(.{id,title,channel,format_id,ext,filesize,filesize_approx})j,"rf":%(requested_formats.:.{format_id,ext,protocol,filesize,filesize_approx})j}
--print
after_move:YDH1 {"k":"moved","i":%(.{id,filepath})j}
```

`moved` replaces the plan's `--print after_move:filepath`. It carries the same path, but as ASCII-safe JSON
(the plain form works too with `--encoding utf-8`, see `no-encoding-flag` for what it looks like without).

### Line kinds

| `k` | Stream | When | Fields |
|---|---|---|---|
| `plan` | stdout | once per video, after format selection, before downloading | `i`: id, title, channel, format_id (`395+251` for a merge), ext, filesize/filesize_approx when known. `rf`: one entry per format to download (empty for a single format). |
| `dl` | stdout | download progress, per format | `p`: status (`downloading` / `finished`), byte counts, speed, eta, elapsed, fragment fields. `i.format_id` says **which** format. |
| `pp` | **stderr** | each post-processor | `p.status` `started` / `finished`, `p.postprocessor` name. `i.filepath` is the file being processed (in the work folder). |
| `moved` | stdout | once, after the file is in the destination | `i.filepath`: the **final** path. |

Any line without the prefix is raw yt-dlp text: `WARNING: …`, `ERROR: …` (stderr). Those go to the output tail
and the error classifier (§5.2).

### Detecting the phase

1. **Downloading:** from the first `dl` line until the last requested format's `dl` `finished`.
   - Merge: two `dl` sequences, one per `format_id`, in `plan.rf` order (video, then audio).
   - Single format: one sequence; `rf` is `[]` and the sizes are in `plan.i`.
2. **Processing:** `pp` `started` for anything other than `MoveFiles`. Post-processors only report
   started/finished, with no percentage, so the UI shows an indeterminate "Processing (Merger)" state.
3. **Moving:** `pp` `MoveFiles` `started`.
4. **Done:** `moved` arrives and the process exits with 0. Take the final path from `moved`, never from
   `dl`/`pp` (those are work-folder paths).

Post-processors seen, in order:

| Case | Sequence |
|---|---|
| merge (Best) | `Merger` → `Metadata` → `EmbedThumbnail` → `MoveFiles` |
| audio MP3 | `ExtractAudio` → `Metadata` → `EmbedThumbnail` → `MoveFiles` |
| HLS | `Merger` → `Metadata` → `EmbedThumbnail` → `MoveFiles` |

Every run also starts with `MetadataParser`. That comes from the harness's `--replace-in-metadata` and does
not happen in production. `Metadata` (FFmpegMetadata) writes the tags **and** the chapters; there is no
separate chapters post-processor. Other names exist (`FixupM3u8`, `FixupM4a`, `FixupDuplicateMoov`,
`ThumbnailsConvertor`, …), so the parser must accept any name.

### Parsing rules (from the captures)

- **Missing keys are omitted, not `null`.** `eta` and `speed` disappear on some lines; `total_bytes` is missing on
  fragment downloads. Every field is optional.
- **Numbers:** ints and floats are mixed (`"total_bytes_estimate": 22784.0`, `"elapsed": 0`). Parse as double.
- **HTTPS formats:** `total_bytes` is exact from the first line.
- **Fragment (HLS) formats:** only `total_bytes_estimate`, which grows as fragments arrive. Use
  `fragment_index / fragment_count` for the percentage. `fragment_index` **can exceed** `fragment_count`
  (`17` of `16`, so clamp), and the `finished` line has the exact `total_bytes`.
- **Overall progress for a merge** = (bytes of finished formats + current `downloaded_bytes`) / sum of the
  `plan.rf` sizes. HLS formats have **no** size in `plan.rf` (only format_id, ext, protocol). Fall back to equal
  weight per format with each format's own fragment fraction.
- `info.requested_formats` is **not** available inside the download template (it comes out as `[]`), which is
  why the sizes come from the `plan` line.
- **Line endings are `\n`** on both streams, even on Windows. Split on `\n` and trim a trailing `\r`.
- **Volume:** without throttling, HLS writes about 115 `dl` lines per format for a 79 s 360p video (231 lines,
  61 KB). `--progress-delta 0.25` brings it to 3 + `finished` per format (8 lines), and the `finished` lines
  are kept. Recommended: pass `--progress-delta 0.25`, which matches the plan's 4 updates/s, and keep the
  engine's coalescing as a safety net.
- `dl` lines shouldn't fill the 200-line output tail of §5.4. Keep only the non-`dl` lines there (`pp`, raw
  text); `dl` lines go only to the full job log.

## Unicode paths and encoding

**What works with `--encoding utf-8`:**

- yt-dlp, Deno and ffmpeg/ffprobe started from `…\Kullanıcı İ 😀 用户\…\tools\`. The verbose log shows
  `JS runtimes: deno-2.9.8`, `[jsc:deno] Solving JS challenges using deno`, `exe versions: ffmpeg …, ffprobe …`,
  and no warnings.
- Partial files in `-P temp:` (with the channel subfolder created under it), a merge, an MP3 conversion and the
  embeds there, then the move to `-P home:`.
- `--cache-dir` in a non-ASCII folder.
- Raw text (`ERROR:` lines, `%(…)s` prints) arrives as valid UTF-8, e.g. sample `error-unicode-path`:
  `ERROR: Unable to create directory: [WinError 3] … 'C:\\…\\Kullanıcı İ 😀 用户\\engel İ 東\\alt'`.

**The encoding problem and its fix:** the verbose header says
`Encodings: locale cp1252, fs utf-8, pref utf-8, out cp1252` with `--encoding utf-8`. Without the flag, `pref` is
cp1252 and yt-dlp encodes its output with `errors=ignore`-like behaviour. Sample `no-encoding-flag`, decoded as
Latin-1:

```
RAW C:\…\work\Kullanc   \Downloads\Youtube ndirgeç\enc-none\jawed\2005-04-24 - zmir lk    Me at the zoo.m4a
```

`ı`, `İ`, emoji and CJK are gone; `ç` and `—` (cp1252 0x97) survive as cp1252 bytes. The file on disk is
correct. Only the reporting is lossy, which is why it would be easy to miss. The four modes, tested on the same run:

| Child environment / flag | Raw text output |
|---|---|
| nothing | cp1252, characters dropped |
| `PYTHONUTF8=1` | same as nothing |
| `PYTHONIOENCODING=utf-8` | same as nothing |
| `--encoding utf-8` | **UTF-8, correct** |

**Decisions:**

- Always pass `--encoding utf-8`. Decode both streams as UTF-8 (with replacement, never throw).
- Set no `PYTHON*` variables, and remove inherited `PYTHON*` variables from the child environment, so a user's
  Python setup can never change the behaviour (they are ignored by the exe today, but that is a PyInstaller detail).
- The `YDH1` lines don't depend on any of this (ASCII JSON), so progress and the final path survive even if
  the flag is ever lost.

**Remaining quirk (harmless):** with `-v`, the very first line `[debug] Command-line config: […]` is written
before `--encoding` applies and drops non-ASCII characters even with the flag. It only affects diagnostics.

## Other findings

- **Existing destination file** (sample `already-exists`): when the final file is already in `-P home:`, yt-dlp
  does **not** download or merge. There are no `dl` lines and no `Merger`, but `Metadata`, `EmbedThumbnail` and
  `MoveFiles` still run and `moved` is printed, exit code 0. A different video with the same
  `<Channel>\<date> - <Title>` would be reported as done without being downloaded, so the plan's
  " [<videoId>]" collision rule (§5.4) must be checked by the engine **before** starting yt-dlp. Also treat
  "`moved` without any `dl` line" as "already present".
- **"Best" container with `--embed-thumbnail`:** AV1/VP9 + Opus would merge to WebM, but yt-dlp prints
  `WARNING: webm doesn't support embedding a thumbnail, mkv will be used`, so Best produces **MKV** (cover as a
  `cover.webp` attachment). That warning is expected and not an error.
- **Work folder:** after a successful run `work\<itemId>\` has no files left, only the empty channel subfolder.
  The engine deletes the folder anyway.
- **Not covered:** YouTube served no DASH-segmented (`http_dash_segments`) formats to the default clients. HLS
  (`m3u8_native`) uses the same fragment downloader and the same progress fields. Paths longer than 260
  characters were not tested (§13 risk "Long or Unicode paths").

## Samples

`docs/spikes/samples/` holds the raw captures (`.gitattributes` keeps the bytes as they are): for each case,
`<case>.stdout.txt`, `<case>.stderr.txt` and `<case>.args.json` (exact arguments, environment, exit code). They
are meant to seed `Ydh.FakeYtDlp` (#36) and the parser tests (#41).

| Case | What it shows |
|---|---|
| `merge` | Best preset: AV1 + Opus → MKV, two `dl` sequences, `Merger`, thumbnail warning |
| `audio-mp3` | single format + `ExtractAudio` to MP3 |
| `hls` | `m3u8_native` video + audio: fragment fields, growing estimates, `fragment_index` > `fragment_count`, no sizes in `plan` |
| `hls-progress-delta` | same with `--progress-delta 0.25` |
| `already-exists` | destination already present: no `dl`, no `Merger`, still `moved` |
| `no-encoding-flag` | no `--encoding`: cp1252 bytes with dropped characters in the raw `RAW …` print line |
| `error-unicode-path` | `ERROR:` line with a non-ASCII path, exit code 1, no stdout |

The paths in the samples are the harness's absolute paths on the test machine.
