# ADR 0001: SQLite owns domain state

## Status

Accepted

## Decision

SQLite stores Categories, Queue Items, Videos, Download Records, Tracked Playlists, Playlist Entries, and Playlist Checks in `app.db` under the application data directory. Schema changes use explicit versioned migrations. Transactions protect ordering and multi-row decisions, while WAL mode permits queue workers and the UI to use the database concurrently.

Scalar user preferences remain in `settings.json`. Cookies, activity logs, and packaged runtime tools remain files because other tools or users consume them directly. `download-archive.txt` remains a file because `yt-dlp` reads and updates it and because archive eligibility is not Download History.

## Consequences

Domain relationships and history can be queried without rewriting whole JSON documents. Existing Category and queue JSON state is imported once. Database-open or migration failure preserves the database and its WAL/SHM companions in a timestamped backup before one fresh-database attempt.
