from __future__ import annotations

import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from ytdlp_helper.config import AppPaths, Category
from ytdlp_helper.database import Database
from ytdlp_helper.tracked_playlist_controller import TrackedPlaylistController

PLAYLIST_URL = "https://www.youtube.com/playlist?list=PLabc123XYZ"
PLAYLIST_URL2 = "https://www.youtube.com/playlist?list=PLxyz789ABCD"
PLAYLIST_URL3 = "https://www.youtube.com/playlist?list=PLrowsLongID"
PLAYLIST_URL4 = "https://www.youtube.com/playlist?list=PLposLongID12"
PLAYLIST_URL5 = "https://www.youtube.com/playlist?list=PLdecideLong1"
PLAYLIST_URL6 = "https://www.youtube.com/playlist?list=PLresetLong123"


def _paths() -> AppPaths:
    root = Path(tempfile.mkdtemp())
    return AppPaths(
        data_dir=root / "data",
        settings_file=root / "data" / "settings.json",
        archive_file=root / "data" / "download-archive.txt",
        cookies_file=root / "data" / "cookies.txt",
        logs_dir=root / "data" / "logs",
        activity_log_file=root / "data" / "logs" / "activity.log",
        tools_dir=root / "data" / "tools",
        ytdlp_executable=root / "data" / "tools" / "yt-dlp.exe",
        ffmpeg_dir=root / "data" / "tools" / "ffmpeg",
        ffmpeg_executable=root / "data" / "tools" / "ffmpeg" / "ffmpeg.exe",
        ffprobe_executable=root / "data" / "tools" / "ffmpeg" / "ffprobe.exe",
        deno_dir=root / "data" / "tools" / "deno",
        deno_executable=root / "data" / "tools" / "deno" / "deno.exe",
        download_dir=root / "downloads",
    )


class TrackedPlaylistControllerTests(unittest.TestCase):
    def setUp(self) -> None:
        self.paths = _paths()
        self.database = Database.for_data_dir(self.paths.data_dir)
        self.database.initialize()
        self.database.import_categories([
            Category("default", "Default", str(self.paths.download_dir)),
        ])
        self.controller = TrackedPlaylistController(self.database, self.paths)

    def test_add_tracker_rejects_invalid_url(self) -> None:
        with self.assertRaises(ValueError):
            self.controller.add_tracker("not-a-url", "best-video", "default")

    def test_add_tracker_creates_tracker(self) -> None:
        tracker_id = self.controller.add_tracker(
            PLAYLIST_URL, "best-video", "default",
        )
        trackers = self.database.trackers()
        ids = [t.id for t in trackers]
        self.assertIn(tracker_id, ids)
        tracker = next(t for t in trackers if t.id == tracker_id)
        self.assertEqual(tracker.playlist_id, "PLabc123XYZ")
        self.assertEqual(tracker.preset, "best-video")
        self.assertTrue(tracker.active)

    def test_toggle_active_stops_and_reactivates(self) -> None:
        tracker_id = self.controller.add_tracker(
            PLAYLIST_URL2, "audio-mp3", "default",
        )
        self.controller.toggle_active(tracker_id, False)
        tracker = next(t for t in self.database.trackers() if t.id == tracker_id)
        self.assertFalse(tracker.active)

        self.controller.toggle_active(tracker_id, True)
        tracker = next(t for t in self.database.trackers() if t.id == tracker_id)
        self.assertTrue(tracker.active)

    def test_reset_tracker_clears_entries(self) -> None:
        tracker_id = self.controller.add_tracker(
            PLAYLIST_URL6, "best-video", "default",
        )
        self.database.record_playlist_check(tracker_id, [
            {"video_id": "vid1", "title": "Video 1", "position": 1, "upload_date": "20260101"},
        ])
        self.controller.reset_tracker(tracker_id)
        candidates = self.database.pending_candidates()
        self.assertEqual(len(candidates), 1)

    def test_update_tracker_changes_preset_and_category(self) -> None:
        tracker_id = self.controller.add_tracker(
            PLAYLIST_URL, "best-video", "default",
        )
        self.controller.update_tracker(tracker_id, "audio-mp3", "default")
        tracker = next(t for t in self.database.trackers() if t.id == tracker_id)
        self.assertEqual(tracker.preset, "audio-mp3")

    def test_state_label_localized(self) -> None:
        self.assertEqual(self.controller.state_label("tr", True), "Aktif")
        self.assertEqual(self.controller.state_label("tr", False), "Durduruldu")
        self.assertEqual(self.controller.state_label("en", True), "Active")

    def test_outcome_label_localized(self) -> None:
        self.assertEqual(self.controller.outcome_label("tr", "success"), "Başarılı")
        self.assertEqual(self.controller.outcome_label("tr", "failed"), "Başarısız")
        self.assertEqual(self.controller.outcome_label("tr", ""), "Kontrol edilmedi")

    def test_check_summary_formats_counts(self) -> None:
        counts = [("Mix", 4, ""), ("News", 0, "offline")]
        self.assertEqual(
            self.controller.check_summary("en", counts),
            "Mix: 4 current\nNews: Failed - offline",
        )
        self.assertEqual(
            self.controller.check_summary("tr", counts),
            "Mix: 4 mevcut\nNews: Başarısız - offline",
        )

    def test_queue_rows_uses_current_tracker_settings(self) -> None:
        self.controller.add_tracker(
            PLAYLIST_URL3, "best-video", "default",
        )
        tracker = self.database.trackers()[0]
        self.database.record_playlist_check(tracker.id, [
            {"video_id": "vid1", "title": "Video 1", "position": 1, "upload_date": "20260101"},
        ])
        candidates = self.database.pending_candidates()
        rows = self.controller.queue_rows(candidates, "%(title)s.%(ext)s")
        self.assertEqual(len(rows), 1)
        self.assertEqual(rows[0]["preset"], "best-video")
        self.assertEqual(rows[0]["playlist_id"], "PLrowsLongID")

        self.controller.update_tracker(tracker.id, "audio-mp3", "default")
        rows = self.controller.queue_rows(candidates, "%(title)s.%(ext)s")
        self.assertEqual(rows[0]["preset"], "audio-mp3")

    def test_queue_rows_includes_position_in_template(self) -> None:
        self.controller.add_tracker(
            PLAYLIST_URL4, "best-video", "default",
        )
        tracker = self.database.trackers()[0]
        self.database.record_playlist_check(tracker.id, [
            {"video_id": "vid1", "title": "Video 1", "position": 3, "upload_date": "20260101"},
        ])
        candidates = self.database.pending_candidates()
        rows = self.controller.queue_rows(candidates, "%(title)s.%(ext)s")
        self.assertIn("3 - ", rows[0]["filename_template"])

    def test_preset_labels_for_language(self) -> None:
        labels = self.controller.preset_labels_for_language("en")
        self.assertIn("Best Video", labels)
        self.assertIn("Audio MP3", labels)
        labels_tr = self.controller.preset_labels_for_language("tr")
        self.assertIn("En İyi Video", labels_tr)
        self.assertIn("Ses MP3", labels_tr)

    def test_preset_key_for_label_round_trip(self) -> None:
        key = self.controller.preset_key_for_label("en", "Audio MP3")
        self.assertEqual(key, "audio-mp3")

    def test_preset_key_for_label_returns_none_for_unknown(self) -> None:
        key = self.controller.preset_key_for_label("en", "NonExistent")
        self.assertIsNone(key)

    def test_decide_entries_dismisses_candidates(self) -> None:
        self.controller.add_tracker(
            PLAYLIST_URL5, "best-video", "default",
        )
        tracker = self.database.trackers()[0]
        self.database.record_playlist_check(tracker.id, [
            {"video_id": "vid1", "title": "Video", "position": 1, "upload_date": "20260101"},
        ])
        candidates = self.controller.pending_candidates()
        self.assertEqual(len(candidates), 1)
        self.controller.decide_entries([candidates[0].entry_id], "dismissed")
        self.assertEqual(len(self.controller.pending_candidates()), 0)

    def test_check_all_skips_when_running(self) -> None:
        self.controller.check_running = True
        on_complete_calls = []
        self.controller.check_all("en", lambda counts: on_complete_calls.append(counts))
        self.assertEqual(len(on_complete_calls), 0)


if __name__ == "__main__":
    unittest.main()
