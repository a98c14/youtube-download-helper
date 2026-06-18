from __future__ import annotations

import sqlite3
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from ytdlp_helper.config import Category
from ytdlp_helper.database import Database, SCHEMA_VERSION
from ytdlp_helper.playlist_tracker import canonical_playlist_url, parse_youtube_playlist_id


class DatabaseTests(unittest.TestCase):
    def setUp(self) -> None:
        self.root = Path(tempfile.mkdtemp())
        self.database = Database(self.root / "app.db")
        self.database.initialize()
        self.database.import_categories([Category("default", "Default", "downloads")])

    def test_schema_is_versioned_and_reopens(self) -> None:
        self.database.initialize()
        with self.database.connect() as connection:
            self.assertEqual(connection.execute("PRAGMA user_version").fetchone()[0], SCHEMA_VERSION)
            self.assertEqual(connection.execute("PRAGMA foreign_keys").fetchone()[0], 1)

    def test_history_keeps_repeated_downloads(self) -> None:
        values = dict(title="Title", category_name="Default", preset="best-video", output_path="x.mp4",
                      extractor="youtube", media_id="abc")
        self.database.add_download_record(**values)
        self.database.add_download_record(**values)
        self.assertEqual(len(self.database.download_history()), 2)

    def test_tracker_lifecycle_pending_recovery_and_reset(self) -> None:
        tracker_id = self.database.add_tracker("PL1234567890", canonical_playlist_url("PL1234567890"),
                                               "List", "best-video", "default")
        entries = [{"video_id": "a", "title": "A", "position": 2, "upload_date": "20260102"},
                   {"video_id": "b", "title": "B", "position": 1, "upload_date": "20260101"}]
        self.database.record_playlist_check(tracker_id, entries)
        candidates = self.database.pending_candidates()
        self.assertEqual([candidate.video_id for candidate in candidates], ["b", "a"])
        self.database.decide_entries([candidate.entry_id for candidate in candidates], "queued")
        self.database.record_playlist_check(tracker_id, None, "offline")
        self.assertEqual(self.database.pending_candidates(), [])
        self.database.reset_tracker(tracker_id)
        self.assertEqual(len(self.database.pending_candidates()), 2)
        self.database.set_tracker_active(tracker_id, False)
        self.assertEqual(self.database.pending_candidates(), [])

    def test_category_deletion_reassigns_tracker(self) -> None:
        self.database.replace_categories([Category("default", "Default", "d"), Category("work", "Work", "w")])
        tracker_id = self.database.add_tracker("PL1234567890", "url", "List", "best-video", "work")
        self.database.replace_categories([Category("default", "Default", "d")])
        self.assertEqual(self.database.trackers()[0].category_id, "default")

    def test_corrupt_database_is_backed_up(self) -> None:
        path = self.root / "broken.db"
        path.write_bytes(b"not sqlite")
        database = Database(path)
        backup = database.initialize_with_recovery()
        self.assertIsNotNone(backup)
        self.assertTrue((backup / "broken.db").exists())  # type: ignore[operator]
        with database.connect() as connection:
            self.assertEqual(connection.execute("PRAGMA user_version").fetchone()[0], SCHEMA_VERSION)


class PlaylistUrlTests(unittest.TestCase):
    def test_requires_youtube_list_identity(self) -> None:
        self.assertEqual(parse_youtube_playlist_id("https://youtube.com/watch?v=x&list=PL1234567890"), "PL1234567890")
        self.assertIsNone(parse_youtube_playlist_id("https://youtube.com/watch?v=x"))
        self.assertIsNone(parse_youtube_playlist_id("https://example.com/?list=PL1234567890"))


if __name__ == "__main__":
    unittest.main()
