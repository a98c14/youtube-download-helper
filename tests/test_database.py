from __future__ import annotations

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

    def test_queue_history_returns_completed_items(self) -> None:
        with self.database.connect() as connection:
            connection.execute(
                "INSERT INTO queue_items(id,position,url,preset,download_dir,filename_template,"
                "added_at,category_id,category_name,status,name,output_path,completed_at,"
                "extractor,media_id) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                ("1", 0, "https://youtube.com/watch?v=abc", "best-video", "downloads",
                 "%(title)s.%(ext)s", "2026-04-24T00:00:00", "default", "Default",
                 "completed", "Test Video", "output.mp4", "2026-04-24T00:01:00",
                 "youtube", "abc"),
            )
        history = self.database.queue_history()
        self.assertEqual(len(history), 1)
        self.assertEqual(history[0].title, "Test Video")

    def test_tracker_lifecycle_and_checks(self) -> None:
        tracker_id = self.database.add_tracker("PL1234567890", canonical_playlist_url("PL1234567890"),
                                               "List", "best-video", "default")
        self.database.record_tracker_check(tracker_id, entry_count=5, new_count=3)
        self.database.record_tracker_check(tracker_id, error="offline")
        trackers = self.database.trackers()
        self.assertEqual(len(trackers), 1)
        self.assertEqual(trackers[0].last_outcome, "failed")
        self.assertIn("offline", trackers[0].last_error)

    def test_category_deletion_reassigns_tracker(self) -> None:
        self.database.replace_categories([Category("default", "Default", "d"), Category("work", "Work", "w")])
        tracker_id = self.database.add_tracker("PL1234567890", "url", "List", "best-video", "work")
        self.database.replace_categories([Category("default", "Default", "d")])
        self.assertEqual(self.database.trackers()[0].category_id, "default")

    def test_stopped_tracker_settings_can_be_updated(self) -> None:
        self.database.replace_categories([Category("default", "Default", "d"), Category("work", "Work", "w")])
        tracker_id = self.database.add_tracker("PL1234567890", "url", "List", "best-video", "default")
        self.database.set_tracker_active(tracker_id, False)

        self.database.update_tracker(tracker_id, preset="audio-mp3", category_id="work")

        tracker = self.database.trackers()[0]
        self.assertFalse(tracker.active)
        self.assertEqual((tracker.preset, tracker.category_id), ("audio-mp3", "work"))

    def test_playlist_title_is_persisted_from_tracker_check(self) -> None:
        tracker_id = self.database.add_tracker("PL1234567890", canonical_playlist_url("PL1234567890"),
                                                "List", "best-video", "default")
        self.database.record_tracker_check(tracker_id, entry_count=3, new_count=1, playlist_title="My Real Playlist")
        trackers = self.database.trackers()
        self.assertEqual(trackers[0].title, "My Real Playlist")

    def test_initialize_with_recovery(self) -> None:
        path = self.root / "broken.db"
        path.write_bytes(b"not sqlite")
        database = Database(path)
        backup = database.initialize_with_recovery()
        self.assertIsNotNone(backup)
        self.assertTrue((backup / "broken.db").exists())
        with database.connect() as connection:
            self.assertEqual(connection.execute("PRAGMA user_version").fetchone()[0], SCHEMA_VERSION)


class PlaylistUrlTests(unittest.TestCase):
    def test_requires_youtube_list_identity(self) -> None:
        self.assertEqual(parse_youtube_playlist_id("https://youtube.com/watch?v=x&list=PL1234567890"), "PL1234567890")
        self.assertIsNone(parse_youtube_playlist_id("https://youtube.com/watch?v=x"))
        self.assertIsNone(parse_youtube_playlist_id("https://example.com/?list=PL1234567890"))


if __name__ == "__main__":
    unittest.main()
