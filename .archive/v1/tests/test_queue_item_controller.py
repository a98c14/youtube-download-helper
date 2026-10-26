from __future__ import annotations

import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from ytdlp_helper.config import AppPaths
from ytdlp_helper.download_queue import QueueItem, QueueRunner, QueueStore
from ytdlp_helper.queue_item_controller import QueueItemController


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


class QueueItemControllerTests(unittest.TestCase):
    def setUp(self) -> None:
        self.paths = _paths()
        self.store = QueueStore.for_paths(self.paths)
        self.store.load()
        self.runner = QueueRunner(
            self.store, self.paths, lambda msg: None,
        )
        self.controller = QueueItemController(self.store, self.runner)

    def test_add_item_and_items(self) -> None:
        item = self.controller.add_item(
            "https://youtube.com/watch?v=test1", "best-video",
            str(self.paths.download_dir), "%(title)s.%(ext)s",
            "default", "Default",
        )
        items = self.controller.items()
        self.assertEqual(len(items), 1)
        self.assertEqual(items[0].id, item.id)
        self.assertEqual(items[0].url, "https://youtube.com/watch?v=test1")

    def test_find_existing(self) -> None:
        self.controller.add_item(
            "https://youtube.com/watch?v=dup", "best-video",
            str(self.paths.download_dir), "%(title)s.%(ext)s",
            "default", "Default", media_id="dup",
        )
        found = self.controller.find_existing(
            "dup", "best-video", str(self.paths.download_dir),
            "%(title)s.%(ext)s", True, "",
        )
        self.assertIsNotNone(found)
        not_found = self.controller.find_existing(
            "other", "best-video", str(self.paths.download_dir),
            "%(title)s.%(ext)s", True, "",
        )
        self.assertIsNone(not_found)

    def test_retry_only_failed_items(self) -> None:
        self.controller.add_item(
            "https://youtube.com/watch?v=retry", "best-video",
            str(self.paths.download_dir), "%(title)s.%(ext)s",
            "default", "Default",
        )
        items = self.controller.items()
        self.assertFalse(self.controller.retry(items[0].id))

    def test_remove_item(self) -> None:
        item = self.controller.add_item(
            "https://youtube.com/watch?v=remove", "best-video",
            str(self.paths.download_dir), "%(title)s.%(ext)s",
            "default", "Default",
        )
        self.assertTrue(self.controller.remove(item.id))
        self.assertEqual(len(self.controller.items()), 0)

    def test_move_item(self) -> None:
        item1 = self.controller.add_item(
            "https://youtube.com/watch?v=first", "best-video",
            str(self.paths.download_dir), "%(title)s.%(ext)s",
            "default", "Default",
        )
        item2 = self.controller.add_item(
            "https://youtube.com/watch?v=second", "best-video",
            str(self.paths.download_dir), "%(title)s.%(ext)s",
            "default", "Default",
        )
        items = self.controller.items()
        self.assertEqual(items[0].id, item1.id)
        self.controller.move(item1.id, 1)
        items = self.controller.items()
        self.assertEqual(items[1].id, item1.id)

    def test_clear_completed(self) -> None:
        self.controller.add_item(
            "https://youtube.com/watch?v=c1", "best-video",
            str(self.paths.download_dir), "%(title)s.%(ext)s",
            "default", "Default",
        )
        self.controller.clear_completed()
        self.assertEqual(len(self.controller.items()), 1)

    def test_get_item_returns_none_for_missing(self) -> None:
        self.assertIsNone(self.controller.get_item("nonexistent"))

    def test_items_matching_filter_all(self) -> None:
        self.controller.add_item(
            "https://youtube.com/watch?v=f1", "best-video",
            str(self.paths.download_dir), "%(title)s.%(ext)s",
            "default", "Default",
        )
        self.assertEqual(len(self.controller.items_matching_filter("all")), 1)
        self.assertEqual(len(self.controller.items_matching_filter("ongoing")), 1)
        self.assertEqual(len(self.controller.items_matching_filter("queued")), 1)
        self.assertEqual(len(self.controller.items_matching_filter("completed")), 0)


if __name__ == "__main__":
    unittest.main()
