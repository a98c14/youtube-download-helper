from __future__ import annotations

import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from ytdlp_helper.config import AppPaths
from ytdlp_helper.downloader import DownloadRequest, DownloadService


class DownloaderTests(unittest.TestCase):
    def test_builds_archive_and_cookie_options(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            paths = AppPaths(
                data_dir=root / "data",
                settings_file=root / "data" / "settings.json",
                archive_file=root / "data" / "download-archive.txt",
                download_dir=root / "downloads",
            )
            paths.data_dir.mkdir(parents=True)
            paths.download_dir.mkdir(parents=True)
            paths.archive_file.touch()

            service = DownloadService(paths)

            with patch("ytdlp_helper.downloader.find_ffmpeg_location", return_value="C:/ffmpeg"):
                options = service._build_options(  # noqa: SLF001
                    DownloadRequest(
                        url="https://www.youtube.com/watch?v=abc123",
                        preset="best-video",
                        browser="chrome",
                        profile="Default",
                    ),
                    lambda *_args: None,
                    lambda *_args: None,
                )

            self.assertEqual(options["download_archive"], str(paths.archive_file))
            self.assertEqual(options["cookiesfrombrowser"], ("chrome", None, None, "Default"))
            self.assertEqual(options["ffmpeg_location"], "C:/ffmpeg")
            self.assertEqual(options["format"], "bv*+ba/b")

    def test_rejects_invalid_url(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            paths = AppPaths(
                data_dir=root / "data",
                settings_file=root / "data" / "settings.json",
                archive_file=root / "data" / "download-archive.txt",
                download_dir=root / "downloads",
            )
            service = DownloadService(paths)

            with self.assertRaises(ValueError):
                service.download(
                    DownloadRequest(url="notaurl", preset="best-video", browser="chrome", profile="Default"),
                    lambda *_args: None,
                    lambda *_args: None,
                )


if __name__ == "__main__":
    unittest.main()
