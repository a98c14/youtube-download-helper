from __future__ import annotations

from datetime import datetime
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from ytdlp_helper.activity_log import ActivityLogStore
from ytdlp_helper.config import AppPaths


class ActivityLogTests(unittest.TestCase):
    def test_appends_timestamped_log_line(self) -> None:
        store = ActivityLogStore(_paths(), now=lambda: datetime(2026, 4, 24, 14, 5, 12))

        line = store.append("Download started")

        self.assertEqual(line, "[2026-04-24 14:05:12] Download started")
        self.assertEqual(store.active_log_file.read_text(encoding="utf-8"), line + "\n")
        self.assertEqual(store.current_session_lines, [line])

    def test_ignores_empty_log_messages(self) -> None:
        store = ActivityLogStore(_paths())

        self.assertIsNone(store.append("  "))
        self.assertFalse(store.active_log_file.exists())

    def test_rotates_active_log_at_size_limit_and_keeps_writing(self) -> None:
        paths = _paths()
        paths.logs_dir.mkdir(parents=True)
        paths.activity_log_file.write_text("12345", encoding="utf-8")
        store = ActivityLogStore(
            paths,
            max_bytes=5,
            now=lambda: datetime(2026, 4, 24, 14, 5, 12),
        )

        store.append("after rotation")

        rotated_file = paths.logs_dir / "activity-20260424-140512.log"
        self.assertEqual(rotated_file.read_text(encoding="utf-8"), "12345")
        self.assertEqual(
            paths.activity_log_file.read_text(encoding="utf-8"),
            "[2026-04-24 14:05:12] after rotation\n",
        )

    def test_read_all_lines_returns_rotated_logs_then_active_log(self) -> None:
        paths = _paths()
        paths.logs_dir.mkdir(parents=True)
        (paths.logs_dir / "activity-20260424-130000.log").write_text("older\n", encoding="utf-8")
        (paths.logs_dir / "activity-20260424-140000.log").write_text("newer\n", encoding="utf-8")
        paths.activity_log_file.write_text("active\n", encoding="utf-8")
        store = ActivityLogStore(paths)

        self.assertEqual(store.read_all_lines(), ["older", "newer", "active"])

    def test_read_current_session_lines_ignores_existing_log_file(self) -> None:
        paths = _paths()
        paths.logs_dir.mkdir(parents=True)
        paths.activity_log_file.write_text("previous session\n", encoding="utf-8")
        store = ActivityLogStore(paths, now=lambda: datetime(2026, 4, 24, 14, 5, 12))

        store.append("current session")

        self.assertEqual(store.read_current_session_lines(), ["[2026-04-24 14:05:12] current session"])


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


if __name__ == "__main__":
    unittest.main()
