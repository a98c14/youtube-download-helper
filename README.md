# Youtube İndirgeç

A Windows download manager for YouTube: a headless engine (`YoutubeDownloadHelper.Engine.exe`) and a WPF app
(`YoutubeDownloadHelper.exe`). The design is in [`docs/PLAN.md`](docs/PLAN.md); how to build and test is in
[`CLAUDE.md`](CLAUDE.md).

## Driving the engine with curl

The engine listens on `127.0.0.1` only and writes its address and a per-start token to
`%LOCALAPPDATA%\YoutubeDownloadHelperData\engine.json` (PLAN.md §6.1). Every request needs the token; requests with
an `Origin` header are refused. The OpenAPI document is at `/openapi/v1.json`.

```powershell
$engine = Get-Content "$env:LOCALAPPDATA\YoutubeDownloadHelperData\engine.json" | ConvertFrom-Json
$api = "$($engine.baseUrl)/api/v1"
$auth = "Authorization: Bearer $($engine.token)"
```

Add videos (URLs or YouTube video IDs; each one is reported as `added`, `duplicate` or `invalid`):

```powershell
'{"urls":["https://www.youtube.com/watch?v=dQw4w9WgXcQ"],"videoIds":["jNQXAC9IVRw"],"presetId":"720p"}' |
    curl.exe -s -H $auth -H "Content-Type: application/json" --data-binary '@-' "$api/items"
```

Look at a URL before adding it: a video's metadata, or a playlist's or channel's entries, each with `duplicate`
(`queued` / `downloaded`) when it is already there. Add a whole playlist or channel with `expandPlaylists`, or only
the videos you picked by their `videoId`:

```powershell
'{"url":"https://www.youtube.com/@channel/videos","limit":50}' |
    curl.exe -s -H $auth -H "Content-Type: application/json" --data-binary '@-' "$api/resolve"
'{"urls":["https://www.youtube.com/playlist?list=PL..."],"expandPlaylists":"all"}' |
    curl.exe -s -H $auth -H "Content-Type: application/json" --data-binary '@-' "$api/items"
```

List the queue (`state`, `search`, `cursor` and `limit` are optional), and look at one item:

```powershell
curl.exe -s -H $auth "$api/items?state=queued,downloading"
curl.exe -s -H $auth "$api/items/1"
curl.exe -s -H $auth "$api/history?search=rick"
```

Pause and resume an item, or the whole queue:

```powershell
curl.exe -s -X POST -H $auth "$api/items/1/pause"
curl.exe -s -X POST -H $auth "$api/items/1/resume"
curl.exe -s -X POST -H $auth "$api/queue/pause"
curl.exe -s -X POST -H $auth "$api/queue/resume"
```

Follow the events (server-sent events; `-N` turns off buffering). Pass the `sequence` from `GET /items` as
`Last-Event-ID` to get every change after that snapshot:

```powershell
curl.exe -s -N -H $auth -H "Last-Event-ID: 42" "$api/events"
```

```
id: 43
event: item.progress
data: {"itemId":1,"progress":{"phase":"downloading","downloadedBytes":1048576,"totalBytes":4194304,...}}
```

Stop the engine (running downloads go back to the queue and continue at the next start):

```powershell
curl.exe -s -X POST -H $auth "$api/engine/shutdown"
```
