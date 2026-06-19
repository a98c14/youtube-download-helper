from __future__ import annotations

import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from ytdlp_helper.config import AppPaths
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

    def test_get_history_returns_empty_when_no_completed_items(self) -> None:
        records = self.viewer.get_history()
        self.assertEqual(records, [])

    def test_get_history_returns_completed_queue_items(self) -> None:
        with self.database.connect() as connection:
            connection.execute(
                "INSERT INTO queue_items(id,position,url,preset,download_dir,filename_template,"
                "added_at,category_id,category_name,status,name,output_path,completed_at,"
                "extractor,media_id) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                ("1", 0, "https://youtube.com/watch?v=abc", "best-video", "downloads",
                 "%(title)s.%(ext)s", "2026-04-24T00:00:00", "default", "Default",
                 "completed", "Test Video", str(self.paths.download_dir / "test.mp4"),
                 "2026-04-24T00:01:00", "youtube", "abc"),
            )
        records = self.viewer.get_history()
        self.assertEqual(len(records), 1)
        self.assertEqual(records[0].title, "Test Video")
        self.assertEqual(records[0].preset, "best-video")

    def test_get_history_returns_multiple_in_order(self) -> None:
        with self.database.connect() as connection:
            connection.execute(
                "INSERT INTO queue_items(id,position,url,preset,download_dir,filename_template,"
                "added_at,category_id,category_name,status,name,output_path,completed_at,"
                "extractor,media_id) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                ("1", 0, "https://youtube.com/watch?v=first", "best-video", "downloads",
                 "%(title)s.%(ext)s", "2026-04-24T00:00:00", "default", "Default",
                 "completed", "First", "first.mp4", "2026-04-24T00:01:00",
                 "youtube", "first"),
            )
            connection.execute(
                "INSERT INTO queue_items(id,position,url,preset,download_dir,filename_template,"
                "added_at,category_id,category_name,status,name,output_path,completed_at,"
                "extractor,media_id) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                ("2", 1, "https://youtube.com/watch?v=second", "audio-mp3", "downloads",
                 "%(title)s.%(ext)s", "2026-04-24T00:00:00", "work", "Work",
                 "completed", "Second", "second.mp3", "2026-04-24T00:02:00",
                 "youtube", "second"),
            )
        records = self.viewer.get_history()
        self.assertEqual(len(records), 2)
        self.assertEqual(records[0].title, "Second")


if __name__ == "__main__":
    unittest.main()
