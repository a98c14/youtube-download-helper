from __future__ import annotations

from contextlib import contextmanager
from dataclasses import dataclass
from datetime import datetime, timezone
import json
from pathlib import Path
import shutil
import sqlite3
from typing import Iterable, Iterator

from .config import Category, DEFAULT_CATEGORY_ID


DATABASE_FILE = "app.db"
SCHEMA_VERSION = 1


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


@dataclass(frozen=True)
class DownloadRecord:
    id: int
    completed_at: str
    title: str
    category_name: str
    preset: str
    output_path: str
    extractor: str = ""
    media_id: str = ""


@dataclass(frozen=True)
class TrackedPlaylist:
    id: int
    playlist_id: str
    url: str
    title: str
    preset: str
    category_id: str
    active: bool
    display_order: int
    last_attempt_at: str = ""
    last_outcome: str = ""
    last_success_at: str = ""
    last_error: str = ""


@dataclass(frozen=True)
class PlaylistCandidate:
    playlist_id: int
    entry_id: int
    video_id: str
    title: str
    position: int | None
    upload_date: str


class Database:
    """SQLite owner for durable domain state.

    Connections are deliberately short-lived. This makes the object safe to use
    from Tk and download worker threads while WAL handles readers during writes.
    """

    def __init__(self, path: Path) -> None:
        self.path = path

    @classmethod
    def for_data_dir(cls, data_dir: Path) -> "Database":
        return cls(data_dir / DATABASE_FILE)

    def initialize(self) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        with self.connect() as connection:
            connection.executescript(_SCHEMA)
            connection.execute(f"PRAGMA user_version = {SCHEMA_VERSION}")
            connection.commit()

    @contextmanager
    def connect(self) -> Iterator[sqlite3.Connection]:
        connection = sqlite3.connect(self.path, timeout=5, isolation_level=None)
        try:
            connection.row_factory = sqlite3.Row
            connection.execute("PRAGMA foreign_keys = ON")
            connection.execute("PRAGMA busy_timeout = 5000")
            connection.execute("PRAGMA journal_mode = WAL")
            yield connection
        finally:
            connection.close()

    def initialize_with_recovery(self) -> Path | None:
        try:
            self.initialize()
            return None
        except sqlite3.DatabaseError:
            stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
            backup_dir = self.path.parent / f"database-backup-{stamp}"
            backup_dir.mkdir(parents=True, exist_ok=True)
            for suffix in ("", "-wal", "-shm"):
                source = Path(f"{self.path}{suffix}")
                if source.exists():
                    shutil.move(str(source), backup_dir / source.name)
            self.initialize()
            return backup_dir

    def import_categories(self, categories: Iterable[Category]) -> None:
        values = list(categories)
        if values and not any(category.id == DEFAULT_CATEGORY_ID for category in values):
            values.insert(0, Category(DEFAULT_CATEGORY_ID, "Default", values[0].download_dir))
        with self.connect() as connection:
            connection.execute("BEGIN IMMEDIATE")
            count = connection.execute("SELECT COUNT(*) FROM categories").fetchone()[0]
            if count == 0:
                connection.executemany(
                    "INSERT INTO categories(id, name, download_dir, display_order, is_default) VALUES(?,?,?,?,?)",
                    [(c.id, c.name, c.download_dir, i, c.id == DEFAULT_CATEGORY_ID) for i, c in enumerate(values)],
                )
            connection.commit()

    def categories(self) -> list[Category]:
        with self.connect() as connection:
            rows = connection.execute(
                "SELECT id, name, download_dir FROM categories ORDER BY display_order, rowid"
            ).fetchall()
        return [Category(row["id"], row["name"], row["download_dir"]) for row in rows]

    def replace_categories(self, categories: Iterable[Category]) -> None:
        values = list(categories)
        if not values:
            raise ValueError("At least one Category is required")
        default = next((c for c in values if c.id == DEFAULT_CATEGORY_ID), values[0])
        with self.connect() as connection:
            connection.execute("BEGIN IMMEDIATE")
            connection.executemany(
                "INSERT INTO categories(id,name,download_dir,display_order,is_default) VALUES(?,?,?,?,?) "
                "ON CONFLICT(id) DO UPDATE SET name=excluded.name,download_dir=excluded.download_dir,"
                "display_order=excluded.display_order,is_default=excluded.is_default",
                [(c.id, c.name, c.download_dir, i, c.id == default.id) for i, c in enumerate(values)],
            )
            connection.execute(
                "UPDATE tracked_playlists SET category_id=? WHERE category_id NOT IN (%s)"
                % ",".join("?" for _ in values),
                [default.id, *(c.id for c in values)],
            )
            connection.execute(
                "DELETE FROM categories WHERE id NOT IN (%s)" % ",".join("?" for _ in values),
                [c.id for c in values],
            )
            connection.commit()

    def add_download_record(
        self, *, title: str, category_name: str, preset: str, output_path: str,
        extractor: str = "", media_id: str = "", queue_item_id: str | None = None,
    ) -> int:
        with self.connect() as connection:
            connection.execute("BEGIN IMMEDIATE")
            video_pk = None
            if extractor and media_id:
                connection.execute(
                    "INSERT INTO videos(extractor,media_id,title) VALUES(?,?,?) "
                    "ON CONFLICT(extractor,media_id) DO UPDATE SET title=excluded.title",
                    (extractor, media_id, title),
                )
                video_pk = connection.execute(
                    "SELECT id FROM videos WHERE extractor=? AND media_id=?", (extractor, media_id)
                ).fetchone()[0]
            cursor = connection.execute(
                "INSERT INTO download_records(video_id,queue_item_id,completed_at,title,category_name,preset,output_path) "
                "VALUES(?,?,?,?,?,?,?)",
                (video_pk, queue_item_id, utc_now(), title, category_name, preset, output_path),
            )
            connection.commit()
            return int(cursor.lastrowid)

    def download_history(self) -> list[DownloadRecord]:
        with self.connect() as connection:
            rows = connection.execute(
                "SELECT d.*, COALESCE(v.extractor,'') extractor, COALESCE(v.media_id,'') media_id "
                "FROM download_records d LEFT JOIN videos v ON v.id=d.video_id ORDER BY d.completed_at DESC,d.id DESC"
            ).fetchall()
        return [DownloadRecord(row["id"], row["completed_at"], row["title"], row["category_name"],
                               row["preset"], row["output_path"], row["extractor"], row["media_id"]) for row in rows]

    def add_tracker(self, playlist_id: str, url: str, title: str, preset: str, category_id: str) -> int:
        with self.connect() as connection:
            order = connection.execute("SELECT COALESCE(MAX(display_order),-1)+1 FROM tracked_playlists").fetchone()[0]
            cursor = connection.execute(
                "INSERT INTO tracked_playlists(playlist_id,url,title,preset,category_id,active,display_order,created_at) "
                "VALUES(?,?,?,?,?,1,?,?)", (playlist_id, url, title, preset, category_id, order, utc_now())
            )
            return int(cursor.lastrowid)

    def trackers(self) -> list[TrackedPlaylist]:
        with self.connect() as connection:
            rows = connection.execute(
                "SELECT p.*, COALESCE(c.attempted_at,'') last_attempt_at,COALESCE(c.outcome,'') last_outcome,"
                "COALESCE(p.last_success_at,'') last_success_at,COALESCE(c.error,'') last_error "
                "FROM tracked_playlists p LEFT JOIN playlist_checks c ON c.id=(SELECT id FROM playlist_checks "
                "WHERE tracked_playlist_id=p.id ORDER BY attempted_at DESC,id DESC LIMIT 1) "
                "ORDER BY p.display_order,p.id"
            ).fetchall()
        return [TrackedPlaylist(row["id"], row["playlist_id"], row["url"], row["title"], row["preset"],
                                row["category_id"], bool(row["active"]), row["display_order"],
                                row["last_attempt_at"], row["last_outcome"], row["last_success_at"],
                                row["last_error"]) for row in rows]

    def update_tracker(self, tracker_id: int, *, preset: str, category_id: str) -> None:
        with self.connect() as connection:
            connection.execute("UPDATE tracked_playlists SET preset=?,category_id=? WHERE id=?", (preset, category_id, tracker_id))

    def set_tracker_active(self, tracker_id: int, active: bool) -> None:
        with self.connect() as connection:
            connection.execute("UPDATE tracked_playlists SET active=? WHERE id=?", (active, tracker_id))

    def reset_tracker(self, tracker_id: int) -> None:
        with self.connect() as connection:
            connection.execute("UPDATE playlist_entries SET decision='pending' WHERE tracked_playlist_id=?", (tracker_id,))

    def record_playlist_check(self, tracker_id: int, entries: Iterable[dict[str, object]] | None,
                              error: str = "") -> None:
        attempted = utc_now()
        values = list(entries or [])
        with self.connect() as connection:
            connection.execute("BEGIN IMMEDIATE")
            if error:
                connection.execute(
                    "INSERT INTO playlist_checks(tracked_playlist_id,attempted_at,outcome,entry_count,new_count,error) VALUES(?,?,'failed',0,0,?)",
                    (tracker_id, attempted, error),
                )
            else:
                connection.execute("UPDATE playlist_entries SET is_current=0 WHERE tracked_playlist_id=?", (tracker_id,))
                for entry in values:
                    connection.execute(
                        "INSERT INTO playlist_entries(tracked_playlist_id,video_id,title,position,upload_date,is_current,decision) "
                        "VALUES(?,?,?,?,?,1,'pending') ON CONFLICT(tracked_playlist_id,video_id) DO UPDATE SET "
                        "title=excluded.title,position=excluded.position,upload_date=excluded.upload_date,is_current=1",
                        (tracker_id, str(entry["video_id"]), str(entry.get("title", "")), entry.get("position"), str(entry.get("upload_date", ""))),
                    )
                pending = connection.execute(
                    "SELECT COUNT(*) FROM playlist_entries WHERE tracked_playlist_id=? AND is_current=1 AND decision='pending'", (tracker_id,)
                ).fetchone()[0]
                connection.execute(
                    "INSERT INTO playlist_checks(tracked_playlist_id,attempted_at,outcome,entry_count,new_count,error) VALUES(?,?,'success',?,?, '')",
                    (tracker_id, attempted, len(values), pending),
                )
                connection.execute("UPDATE tracked_playlists SET last_success_at=? WHERE id=?", (attempted, tracker_id))
            connection.commit()

    def pending_candidates(self) -> list[PlaylistCandidate]:
        with self.connect() as connection:
            rows = connection.execute(
                "SELECT e.tracked_playlist_id,e.id,e.video_id,e.title,e.position,COALESCE(e.upload_date,'') upload_date "
                "FROM playlist_entries e JOIN tracked_playlists p ON p.id=e.tracked_playlist_id "
                "WHERE p.active=1 AND e.is_current=1 AND e.decision='pending' "
                "ORDER BY CASE WHEN e.upload_date='' THEN 1 ELSE 0 END,e.upload_date,p.display_order,e.position,e.id"
            ).fetchall()
        return [PlaylistCandidate(row[0], row[1], row[2], row[3], row[4], row[5]) for row in rows]

    def decide_entries(self, entry_ids: Iterable[int], decision: str) -> None:
        ids = list(entry_ids)
        if not ids:
            return
        if decision not in {"queued", "dismissed", "pending"}:
            raise ValueError("Invalid Playlist Entry decision")
        with self.connect() as connection:
            connection.execute("BEGIN IMMEDIATE")
            connection.execute(
                f"UPDATE playlist_entries SET decision=? WHERE id IN ({','.join('?' for _ in ids)})",
                [decision, *ids],
            )
            connection.commit()


_SCHEMA = """
CREATE TABLE IF NOT EXISTS categories(
 id TEXT PRIMARY KEY, name TEXT NOT NULL, download_dir TEXT NOT NULL,
 display_order INTEGER NOT NULL, is_default INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS queue_items(
 id TEXT PRIMARY KEY, position INTEGER NOT NULL, url TEXT NOT NULL, preset TEXT NOT NULL,
 playlist INTEGER NOT NULL, download_dir TEXT NOT NULL, filename_template TEXT NOT NULL,
 added_at TEXT NOT NULL, category_id TEXT NOT NULL, category_name TEXT NOT NULL,
 status TEXT NOT NULL, name TEXT NOT NULL DEFAULT '', progress INTEGER, speed TEXT NOT NULL DEFAULT '',
 error TEXT NOT NULL DEFAULT '', output_path TEXT NOT NULL DEFAULT '', warning TEXT NOT NULL DEFAULT '',
 source_type TEXT NOT NULL DEFAULT 'manual', playlist_id TEXT, playlist_position INTEGER
);
CREATE TABLE IF NOT EXISTS videos(
 id INTEGER PRIMARY KEY, extractor TEXT NOT NULL, media_id TEXT NOT NULL, title TEXT NOT NULL DEFAULT '',
 UNIQUE(extractor,media_id)
);
CREATE TABLE IF NOT EXISTS download_records(
 id INTEGER PRIMARY KEY, video_id INTEGER REFERENCES videos(id), queue_item_id TEXT,
 completed_at TEXT NOT NULL, title TEXT NOT NULL, category_name TEXT NOT NULL,
 preset TEXT NOT NULL, output_path TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS tracked_playlists(
 id INTEGER PRIMARY KEY, playlist_id TEXT NOT NULL UNIQUE, url TEXT NOT NULL, title TEXT NOT NULL DEFAULT '',
 preset TEXT NOT NULL, category_id TEXT NOT NULL REFERENCES categories(id), active INTEGER NOT NULL DEFAULT 1,
 display_order INTEGER NOT NULL, created_at TEXT NOT NULL, last_success_at TEXT
);
CREATE TABLE IF NOT EXISTS playlist_entries(
 id INTEGER PRIMARY KEY, tracked_playlist_id INTEGER NOT NULL REFERENCES tracked_playlists(id) ON DELETE CASCADE,
 video_id TEXT NOT NULL, title TEXT NOT NULL DEFAULT '', position INTEGER, upload_date TEXT NOT NULL DEFAULT '',
 is_current INTEGER NOT NULL DEFAULT 1, decision TEXT NOT NULL DEFAULT 'pending',
 UNIQUE(tracked_playlist_id,video_id)
);
CREATE TABLE IF NOT EXISTS playlist_checks(
 id INTEGER PRIMARY KEY, tracked_playlist_id INTEGER NOT NULL REFERENCES tracked_playlists(id) ON DELETE CASCADE,
 attempted_at TEXT NOT NULL, outcome TEXT NOT NULL, entry_count INTEGER NOT NULL,
 new_count INTEGER NOT NULL, error TEXT NOT NULL DEFAULT ''
);
"""
