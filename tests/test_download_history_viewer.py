from __future__ import annotations

import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from ytdlp_helper.config import AppPaths, Category
from ytdlp_helper.database import Database
from ytdlp_helper.download_history_viewer import DownloadHistoryViewer


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


class DownloadHistoryViewerTests(unittest.TestCase):
    def setUp(self) -> None:
        self.paths = _paths()
        self.database = Database.for_data_dir(self.paths.data_dir)
        self.database.initialize()
        self.viewer = DownloadHistoryViewer(self.database)

    def test_get_history_returns_empty_when_no_records(self) -> None:
        records = self.viewer.get_history()
        self.assertEqual(records, [])

    def test_get_history_returns_saved_records(self) -> None:
        self.database.add_download_record(
            title="Test Video", category_name="Default", preset="best-video",
            output_path=str(self.paths.download_dir / "test.mp4"),
        )
        records = self.viewer.get_history()
        self.assertEqual(len(records), 1)
        self.assertEqual(records[0].title, "Test Video")
        self.assertEqual(records[0].preset, "best-video")

    def test_get_history_returns_multiple_records_in_order(self) -> None:
        self.database.add_download_record(
            title="First", category_name="Default", preset="best-video",
            output_path=str(self.paths.download_dir / "first.mp4"),
        )
        self.database.add_download_record(
            title="Second", category_name="Work", preset="audio-mp3",
            output_path=str(self.paths.download_dir / "second.mp3"),
        )
        records = self.viewer.get_history()
        self.assertEqual(len(records), 2)
        self.assertEqual(records[0].title, "Second")


if __name__ == "__main__":
    unittest.main()
