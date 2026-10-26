from __future__ import annotations

import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from ytdlp_helper.config import AppPaths
from ytdlp_helper.cookies import CookiePhase, CookieStatus, get_cookie_status, is_valid_netscape_cookie_text, save_cookie_text


VALID_COOKIES = "# Netscape HTTP Cookie File\n.youtube.com\tTRUE\t/\tTRUE\t0\tSID\tabc123\n"


class CookieTests(unittest.TestCase):
    def test_accepts_valid_netscape_cookie_text(self) -> None:
        self.assertTrue(is_valid_netscape_cookie_text(VALID_COOKIES))

    def test_rejects_empty_or_random_text(self) -> None:
        self.assertFalse(is_valid_netscape_cookie_text(""))
        self.assertFalse(is_valid_netscape_cookie_text("not cookie text"))
        self.assertFalse(is_valid_netscape_cookie_text("# only a comment"))

    def test_saves_valid_pasted_cookies(self) -> None:
        paths = _paths()

        save_cookie_text(paths, VALID_COOKIES)

        self.assertEqual(paths.cookies_file.read_text(encoding="utf-8"), VALID_COOKIES)

    def test_failed_validation_leaves_existing_cookie_file_unchanged(self) -> None:
        paths = _paths()
        paths.data_dir.mkdir(parents=True)
        paths.cookies_file.write_text(VALID_COOKIES, encoding="utf-8")

        with self.assertRaises(ValueError):
            save_cookie_text(paths, "invalid")

        self.assertEqual(paths.cookies_file.read_text(encoding="utf-8"), VALID_COOKIES)

    def test_status_reports_missing_vs_saved_timestamp(self) -> None:
        paths = _paths()

        status = get_cookie_status(paths)
        self.assertEqual(status.phase, CookiePhase.NONE)
        self.assertIsNone(status.timestamp)

        save_cookie_text(paths, VALID_COOKIES)

        status = get_cookie_status(paths)
        self.assertEqual(status.phase, CookiePhase.SAVED)
        self.assertIsNotNone(status.timestamp)
        self.assertRegex(status.timestamp or "", r"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$")


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
