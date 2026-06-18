# Context

## Glossary

- `Creator`: YouTube source identity used for top-level organization; resolved from `channel`, falling back to `uploader`.
- `Playlist Context`: Playlist metadata attached to a single video URL; distinct from downloading an entire playlist.
- `Download Organization`: Folder policy controlled by settings, separate from filename format.
- `Category`: A user-defined label and destination folder selected when creating a Queue Item. The Queue Item retains that selection even if the Category is later changed or removed.
- `Runtime Tools`: App-managed executables required for downloads: `yt-dlp`, `ffmpeg`/`ffprobe`, and Deno.
- `Queue Item`: A user-created download entry shown in the queue table; it may be queued, running, completed, failed, or skipped.
