# Context

## Glossary

- `Creator`: YouTube source identity used for top-level organization; resolved from `channel`, falling back to `uploader`.
- `Playlist Context`: Playlist metadata attached to a single video URL; distinct from downloading an entire playlist.
- `Download Organization`: Folder policy controlled by settings, separate from filename format.
- `Category`: A user-defined label and destination folder selected when creating a Queue Item. The Queue Item retains that selection even if the Category is later changed or removed.
- `Runtime Tools`: App-managed executables required for downloads: `yt-dlp`, `ffmpeg`/`ffprobe`, and Deno.
- `Queue Item`: A user-created download entry shown in the queue table; it may be queued, running, completed, failed, or skipped.
- `Video`: A media identity defined by an extractor and that extractor's media ID; it is independent of any particular download or playlist membership.
- `Download Record`: An immutable record that one media file was successfully produced, including its completion time and output path.
- `Download History`: The chronological collection of Download Records. Repeated downloads produce repeated records.
- `Download Archive`: The `yt-dlp` eligibility file that prevents selected media from downloading again. It is separate from Download History.
- `Tracked Playlist`: A YouTube playlist whose current membership is checked manually, with an immutable playlist identity and mutable download settings.
- `Playlist Entry`: A video's membership in one Tracked Playlist, including its current position and the user's pending, queued, or dismissed decision.
- `Playlist Check`: One attempted observation of a Tracked Playlist, whether successful or failed.
- `Playlist Position`: The latest observed one-based ordering of a Playlist Entry in its playlist.
- `Queue State`: The queue-level execution mode shown near the pause/resume controls; one of Pausing, Paused, Running, Waiting, or Idle. It is derived from whether the user has explicitly paused and whether any Queue Items are running or queued.
- `Playlist Download`: A Queue Item that asks `yt-dlp` to download an entire playlist, distinct from individual tracker-created Queue Items.
