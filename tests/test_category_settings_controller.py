from __future__ import annotations

import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from ytdlp_helper.category_settings_controller import CategorySettingsController
from ytdlp_helper.config import AppPaths, Category, DEFAULT_FILENAME_TEMPLATE
from ytdlp_helper.database import Database


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


class CategorySettingsControllerTests(unittest.TestCase):
    def setUp(self) -> None:
        self.paths = _paths()
        self.database = Database.for_data_dir(self.paths.data_dir)
        self.database.initialize()
        self.controller = CategorySettingsController(self.database)

    def test_validate_download_folder_rejects_empty(self) -> None:
        self.assertIsNone(self.controller.validate_download_folder(""))

    def test_validate_download_folder_expands_path(self) -> None:
        result = self.controller.validate_download_folder("~/test-dl")
        self.assertIsNotNone(result)
        self.assertTrue(str(result).endswith("test-dl"))

    def test_validate_filename_template_rejects_empty(self) -> None:
        self.assertIsNone(self.controller.validate_filename_template(""))

    def test_validate_filename_template_rejects_missing_ext(self) -> None:
        self.assertIsNone(self.controller.validate_filename_template("%(title)s"))

    def test_validate_filename_template_rejects_path_separators(self) -> None:
        self.assertIsNone(self.controller.validate_filename_template("nested/%(title)s.%(ext)s"))

    def test_validate_filename_template_accepts_valid(self) -> None:
        result = self.controller.validate_filename_template("%(title)s.%(ext)s")
        self.assertEqual(result, "%(title)s.%(ext)s")

    def test_validate_queue_concurrency_clamps_low(self) -> None:
        self.assertEqual(self.controller.validate_queue_concurrency(0), 1)

    def test_validate_queue_concurrency_clamps_high(self) -> None:
        self.assertEqual(self.controller.validate_queue_concurrency(10), 4)

    def test_validate_queue_concurrency_accepts_valid(self) -> None:
        self.assertEqual(self.controller.validate_queue_concurrency(2), 2)

    def test_validate_queue_concurrency_defaults_on_invalid(self) -> None:
        self.assertEqual(self.controller.validate_queue_concurrency("abc"), 1)

    def test_validate_categories_rejects_empty(self) -> None:
        self.assertIsNone(self.controller.validate_categories([]))

    def test_validate_categories_rejects_unnamed(self) -> None:
        self.assertIsNone(self.controller.validate_categories([
            Category("c1", "", str(self.paths.download_dir)),
        ]))

    def test_validate_categories_accepts_valid(self) -> None:
        result = self.controller.validate_categories([
            Category("c1", "Work", str(self.paths.download_dir)),
        ])
        self.assertIsNotNone(result)
        self.assertEqual(len(result), 1)

    def test_language_code_for_label(self) -> None:
        pairs = [("Turkish", "tr"), ("English", "en")]
        self.assertEqual(self.controller.language_code_for_label(pairs, "Turkish"), "tr")
        self.assertEqual(self.controller.language_code_for_label(pairs, "English"), "en")
        self.assertIsNone(self.controller.language_code_for_label(pairs, "Unknown"))

    def test_language_label_for_code(self) -> None:
        pairs = [("Turkish", "tr"), ("English", "en")]
        self.assertEqual(self.controller.language_label_for_code(pairs, "tr"), "Turkish")
        self.assertEqual(self.controller.language_label_for_code(pairs, "en"), "English")
        self.assertEqual(self.controller.language_label_for_code(pairs, "fr"), "Turkish")

    def test_selected_category_finds_by_id(self) -> None:
        cats = [Category("a", "A", "/a"), Category("b", "B", "/b")]
        result = self.controller.selected_category(cats, "b")
        self.assertEqual(result.id, "b")

    def test_selected_category_falls_back_to_first(self) -> None:
        cats = [Category("a", "A", "/a")]
        result = self.controller.selected_category(cats, "nonexistent")
        self.assertEqual(result.id, "a")

    def test_load_categories_returns_empty_when_no_db_state(self) -> None:
        cats = self.controller.load_categories()
        self.assertEqual(cats, [])

    def test_load_categories_after_import(self) -> None:
        self.database.import_categories([
            Category("default", "Default", str(self.paths.download_dir)),
            Category("work", "Work", str(self.paths.download_dir)),
        ])
        cats = self.controller.load_categories()
        self.assertEqual(len(cats), 2)


if __name__ == "__main__":
    unittest.main()
