from __future__ import annotations

import json
import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from ytdlp_helper.config import AppPaths, get_app_paths, load_settings


class ConfigTests(unittest.TestCase):
    def test_default_download_folder_uses_project_slug(self) -> None:
        with patch.dict(os.environ, {"LOCALAPPDATA": r"C:\Users\tester\AppData\Local"}):
            paths = get_app_paths()

        self.assertEqual(paths.download_dir.name, "youtube-download-helper")
        self.assertEqual(paths.data_dir.name, "YT-DLP Helper")

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


def _paths() -> AppPaths:
    root = Path(tempfile.mkdtemp())
    return AppPaths(
        data_dir=root / "data",
        settings_file=root / "data" / "settings.json",
        archive_file=root / "data" / "download-archive.txt",
        cookies_file=root / "data" / "cookies.txt",
        download_dir=root / "downloads" / "youtube-download-helper",
    )


if __name__ == "__main__":
    unittest.main()
