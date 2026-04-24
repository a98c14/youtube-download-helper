from __future__ import annotations

import json
import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from ytdlp_helper.config import AppPaths, Settings, find_ffmpeg_location, get_app_paths, load_settings


class ConfigTests(unittest.TestCase):
    def test_default_download_folder_uses_project_slug(self) -> None:
        with patch.dict(os.environ, {"LOCALAPPDATA": r"C:\Users\tester\AppData\Local"}):
            paths = get_app_paths()

        self.assertEqual(paths.download_dir.name, "youtube-download-helper")
        self.assertEqual(paths.data_dir.name, "YT-DLP Helper")
        self.assertEqual(paths.logs_dir, paths.data_dir / "logs")
        self.assertEqual(paths.activity_log_file, paths.logs_dir / "activity.log")
        self.assertEqual(paths.tools_dir, paths.data_dir / "tools")
        self.assertEqual(paths.ytdlp_executable, paths.tools_dir / "yt-dlp.exe")
        self.assertEqual(paths.ffmpeg_dir, paths.tools_dir / "ffmpeg")
        self.assertEqual(paths.ffmpeg_executable, paths.ffmpeg_dir / "ffmpeg.exe")
        self.assertEqual(paths.ffprobe_executable, paths.ffmpeg_dir / "ffprobe.exe")

    def test_load_settings_uses_saved_download_dir(self) -> None:
        paths = _paths()
        saved_download_dir = paths.data_dir.parent / "custom-downloads"
        paths.data_dir.mkdir(parents=True)
        paths.settings_file.write_text(
            json.dumps({"preset": "audio-mp3", "download_dir": str(saved_download_dir)}),
            encoding="utf-8",
        )

        settings = load_settings(paths)

        self.assertEqual(settings.preset, "audio-mp3")
        self.assertEqual(settings.download_dir, str(saved_download_dir))
        self.assertEqual(settings.language, "tr")

    def test_default_settings_use_turkish_language(self) -> None:
        settings = Settings()

        self.assertEqual(settings.language, "tr")

    def test_load_settings_uses_saved_valid_language(self) -> None:
        paths = _paths()
        paths.data_dir.mkdir(parents=True)
        paths.settings_file.write_text(
            json.dumps({"preset": "audio-mp3", "download_dir": "downloads", "language": "en"}),
            encoding="utf-8",
        )

        settings = load_settings(paths)

        self.assertEqual(settings.language, "en")

    def test_load_settings_falls_back_to_turkish_for_invalid_language(self) -> None:
        paths = _paths()
        paths.data_dir.mkdir(parents=True)
        paths.settings_file.write_text(
            json.dumps({"preset": "audio-mp3", "download_dir": "downloads", "language": "de"}),
            encoding="utf-8",
        )

        settings = load_settings(paths)

        self.assertEqual(settings.language, "tr")

    def test_find_ffmpeg_location_falls_back_to_path_when_ffmpeg_and_ffprobe_exist(self) -> None:
        paths = _paths()
        tools_dir = paths.data_dir.parent / "path-tools"
        ffmpeg = tools_dir / "ffmpeg.exe"
        ffprobe = tools_dir / "ffprobe.exe"

        with patch("ytdlp_helper.config.shutil.which") as which:
            which.side_effect = lambda name: {
                "ffmpeg.exe": str(ffmpeg),
                "ffmpeg": None,
                "ffprobe.exe": str(ffprobe),
                "ffprobe": None,
            }[name]

            self.assertEqual(find_ffmpeg_location(paths), str(tools_dir))

    def test_find_ffmpeg_location_does_not_use_path_without_ffprobe(self) -> None:
        paths = _paths()
        ffmpeg = paths.data_dir.parent / "path-tools" / "ffmpeg.exe"

        with patch("ytdlp_helper.config.shutil.which") as which:
            which.side_effect = lambda name: {
                "ffmpeg.exe": str(ffmpeg),
                "ffmpeg": None,
                "ffprobe.exe": None,
                "ffprobe": None,
            }[name]

            self.assertIsNone(find_ffmpeg_location(paths))


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
        download_dir=root / "downloads" / "youtube-download-helper",
    )


if __name__ == "__main__":
    unittest.main()
