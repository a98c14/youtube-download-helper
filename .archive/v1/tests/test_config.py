from __future__ import annotations

import json
import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from ytdlp_helper.config import (
    DEFAULT_FILENAME_TEMPLATE,
    LEGACY_FILENAME_TEMPLATE,
    AppPaths,
    Category,
    Settings,
    factory_reset,
    find_ffmpeg_location,
    get_app_paths,
    load_settings,
    save_settings,
)


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
        self.assertEqual(paths.deno_dir, paths.tools_dir / "deno")
        self.assertEqual(paths.deno_executable, paths.deno_dir / "deno.exe")

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

    def test_legacy_settings_create_default_category_from_saved_download_dir(self) -> None:
        paths = _paths()
        saved_download_dir = paths.data_dir.parent / "custom-downloads"
        paths.data_dir.mkdir(parents=True)
        paths.settings_file.write_text(
            json.dumps({"download_dir": str(saved_download_dir)}),
            encoding="utf-8",
        )

        settings = load_settings(paths)

        self.assertEqual(len(settings.categories), 1)
        self.assertEqual(settings.categories[0].name, "Default")
        self.assertEqual(settings.categories[0].download_dir, str(saved_download_dir))
        self.assertEqual(settings.selected_category_id, settings.categories[0].id)

    def test_categories_round_trip_and_invalid_selection_falls_back(self) -> None:
        paths = _paths()
        categories = [
            Category("work", "Work", str(paths.data_dir.parent / "work")),
            Category("home", "Home", str(paths.data_dir.parent / "home")),
        ]
        save_settings(
            paths,
            Settings(categories=categories, selected_category_id="missing", download_dir=categories[0].download_dir),
        )

        settings = load_settings(paths)

        self.assertEqual(settings.categories, categories)
        self.assertEqual(settings.selected_category_id, "work")

    def test_invalid_category_rows_normalize_to_default(self) -> None:
        paths = _paths()
        paths.data_dir.mkdir(parents=True)
        paths.settings_file.write_text(
            json.dumps({"download_dir": "legacy", "categories": [{"id": "", "name": "", "download_dir": ""}]}),
            encoding="utf-8",
        )

        settings = load_settings(paths)

        self.assertEqual(settings.categories, [Category("default", "Default", "legacy")])

    def test_default_settings_use_turkish_language(self) -> None:
        settings = Settings()

        self.assertEqual(settings.language, "tr")
        self.assertTrue(settings.organize_by_channel)
        self.assertEqual(settings.filename_template, DEFAULT_FILENAME_TEMPLATE)
        self.assertEqual(settings.queue_concurrency, 1)
        self.assertTrue(settings.organize_by_channel)

    def test_load_settings_uses_saved_organize_by_channel(self) -> None:
        for value in (True, False):
            with self.subTest(value=value):
                paths = _paths()
                paths.data_dir.mkdir(parents=True)
                paths.settings_file.write_text(
                    json.dumps({"download_dir": "downloads", "organize_by_channel": value}),
                    encoding="utf-8",
                )

                settings = load_settings(paths)

                self.assertIs(settings.organize_by_channel, value)

    def test_load_settings_defaults_legacy_organize_by_channel_to_true(self) -> None:
        paths = _paths()
        paths.data_dir.mkdir(parents=True)
        paths.settings_file.write_text(json.dumps({"download_dir": "downloads"}), encoding="utf-8")

        settings = load_settings(paths)

        self.assertTrue(settings.organize_by_channel)

    def test_load_settings_uses_saved_filename_template(self) -> None:
        paths = _paths()
        paths.data_dir.mkdir(parents=True)
        paths.settings_file.write_text(
            json.dumps(
                {
                    "preset": "audio-mp3",
                    "download_dir": "downloads",
                    "filename_template": "%(upload_date)s - %(title)s.%(ext)s",
                }
            ),
            encoding="utf-8",
        )

        settings = load_settings(paths)

        self.assertEqual(settings.filename_template, "%(upload_date)s - %(title)s.%(ext)s")

    def test_load_settings_falls_back_to_default_filename_template_when_blank(self) -> None:
        paths = _paths()
        paths.data_dir.mkdir(parents=True)
        paths.settings_file.write_text(
            json.dumps({"preset": "audio-mp3", "download_dir": "downloads", "filename_template": "  "}),
            encoding="utf-8",
        )

        settings = load_settings(paths)

        self.assertEqual(settings.filename_template, DEFAULT_FILENAME_TEMPLATE)

    def test_load_settings_normalizes_legacy_default_filename_template(self) -> None:
        paths = _paths()
        paths.data_dir.mkdir(parents=True)
        paths.settings_file.write_text(
            json.dumps({"download_dir": "downloads", "filename_template": LEGACY_FILENAME_TEMPLATE}),
            encoding="utf-8",
        )

        settings = load_settings(paths)

        self.assertEqual(settings.filename_template, DEFAULT_FILENAME_TEMPLATE)

    def test_load_settings_preserves_custom_filename_template(self) -> None:
        paths = _paths()
        paths.data_dir.mkdir(parents=True)
        paths.settings_file.write_text(
            json.dumps({"download_dir": "downloads", "filename_template": "%(upload_date)s - %(title)s.%(ext)s"}),
            encoding="utf-8",
        )

        settings = load_settings(paths)

        self.assertEqual(settings.filename_template, "%(upload_date)s - %(title)s.%(ext)s")

    def test_default_filename_template_is_title_only(self) -> None:
        self.assertEqual(DEFAULT_FILENAME_TEMPLATE, "%(title)s.%(ext)s")

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

    def test_load_settings_uses_saved_valid_queue_concurrency(self) -> None:
        paths = _paths()
        paths.data_dir.mkdir(parents=True)
        paths.settings_file.write_text(
            json.dumps({"download_dir": "downloads", "queue_concurrency": 4}),
            encoding="utf-8",
        )

        settings = load_settings(paths)

        self.assertEqual(settings.queue_concurrency, 4)

    def test_load_settings_falls_back_for_invalid_queue_concurrency(self) -> None:
        for value in (0, 5, "bad"):
            with self.subTest(value=value):
                paths = _paths()
                paths.data_dir.mkdir(parents=True)
                paths.settings_file.write_text(
                    json.dumps({"download_dir": "downloads", "queue_concurrency": value}),
                    encoding="utf-8",
                )

                settings = load_settings(paths)

                self.assertEqual(settings.queue_concurrency, 1)

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

    def test_factory_reset_deletes_database_and_wal_shm(self) -> None:
        paths = _paths()
        paths.data_dir.mkdir(parents=True)
        (paths.data_dir / "app.db").write_text("data")
        (paths.data_dir / "app.db-wal").write_text("wal")
        (paths.data_dir / "app.db-shm").write_text("shm")

        errors = factory_reset(paths)

        self.assertEqual(errors, [])
        self.assertFalse((paths.data_dir / "app.db").exists())
        self.assertFalse((paths.data_dir / "app.db-wal").exists())
        self.assertFalse((paths.data_dir / "app.db-shm").exists())

    def test_factory_reset_deletes_state_files(self) -> None:
        paths = _paths()
        paths.data_dir.mkdir(parents=True)
        paths.settings_file.write_text(json.dumps({"preset": "audio-mp3"}))
        paths.archive_file.write_text("youtube abc123\n")
        paths.cookies_file.write_text("cookies")
        paths.logs_dir.mkdir(parents=True)
        (paths.data_dir / "queue.json").write_text("[]")
        (paths.data_dir / "queue.json.migrated").write_text("[]")
        (paths.logs_dir / "activity.log").write_text("log")
        (paths.logs_dir / "activity-20250101.log").write_text("rotated")

        errors = factory_reset(paths)

        self.assertEqual(errors, [])
        self.assertFalse(paths.cookies_file.exists())
        self.assertFalse((paths.data_dir / "queue.json").exists())
        self.assertFalse((paths.data_dir / "queue.json.migrated").exists())
        self.assertFalse((paths.logs_dir / "activity.log").exists())
        self.assertFalse((paths.logs_dir / "activity-20250101.log").exists())
        settings = load_settings(paths)
        self.assertEqual(settings.preset, "best-video")
        self.assertEqual(settings.language, "tr")
        self.assertTrue(paths.data_dir.exists())

    def test_factory_reset_preserves_tools(self) -> None:
        paths = _paths()
        paths.data_dir.mkdir(parents=True)
        paths.tools_dir.mkdir(parents=True)
        ytdlp = paths.tools_dir / "yt-dlp.exe"
        ytdlp.write_text("tool")
        ffmpeg_dir = paths.tools_dir / "ffmpeg"
        ffmpeg_dir.mkdir(parents=True)
        (ffmpeg_dir / "ffmpeg.exe").write_text("tool")

        errors = factory_reset(paths)

        self.assertEqual(errors, [])
        self.assertTrue(ytdlp.exists())
        self.assertTrue((ffmpeg_dir / "ffmpeg.exe").exists())

    def test_factory_reset_preserves_database_backups(self) -> None:
        paths = _paths()
        paths.data_dir.mkdir(parents=True)
        backup_dir = paths.data_dir / "database-backup-20250101"
        backup_dir.mkdir(parents=True)
        (backup_dir / "app.db").write_text("backup")

        errors = factory_reset(paths)

        self.assertEqual(errors, [])
        self.assertTrue(backup_dir.exists())
        self.assertTrue((backup_dir / "app.db").exists())

    def test_factory_reset_preserves_download_dir_contents(self) -> None:
        paths = _paths()
        paths.data_dir.mkdir(parents=True)
        paths.download_dir.mkdir(parents=True)
        media_file = paths.download_dir / "video.mp4"
        media_file.write_text("media-content")

        errors = factory_reset(paths)

        self.assertEqual(errors, [])
        self.assertTrue(media_file.exists())

    def test_factory_reset_recreates_dirs_and_default_settings(self) -> None:
        paths = _paths()
        paths.data_dir.mkdir(parents=True)

        errors = factory_reset(paths)

        self.assertEqual(errors, [])
        self.assertTrue(paths.logs_dir.exists())
        settings = load_settings(paths)
        self.assertEqual(settings.preset, "best-video")
        self.assertEqual(settings.language, "tr")
        self.assertEqual(settings.filename_template, DEFAULT_FILENAME_TEMPLATE)
        self.assertEqual(settings.queue_concurrency, 1)
        self.assertTrue(settings.organize_by_channel)
        self.assertEqual(len(settings.categories), 1)
        self.assertEqual(settings.categories[0].id, "default")
        self.assertEqual(settings.categories[0].name, "Default")

    def test_factory_reset_returns_empty_errors_on_success(self) -> None:
        paths = _paths()
        paths.data_dir.mkdir(parents=True)

        errors = factory_reset(paths)

        self.assertEqual(errors, [])

    def test_factory_reset_reports_file_deletion_errors(self) -> None:
        paths = _paths()
        paths.data_dir.mkdir(parents=True)
        paths.settings_file.write_text("{}")

        with patch("pathlib.Path.unlink", side_effect=OSError("permission denied")):
            errors = factory_reset(paths)

        self.assertTrue(len(errors) > 0)
        self.assertTrue(any("permission denied" in err for err in errors))

    def test_factory_reset_reports_recreate_errors(self) -> None:
        paths = _paths()
        paths.data_dir.mkdir(parents=True)

        with patch("ytdlp_helper.config.ensure_app_dirs", side_effect=OSError("disk full")):
            errors = factory_reset(paths)

        self.assertTrue(len(errors) > 0)
        self.assertTrue(any("disk full" in err for err in errors))

    def test_factory_reset_reports_settings_write_errors(self) -> None:
        paths = _paths()
        paths.data_dir.mkdir(parents=True)

        with patch("ytdlp_helper.config.save_settings", side_effect=OSError("read-only")):
            errors = factory_reset(paths)

        self.assertTrue(len(errors) > 0)
        self.assertTrue(any("read-only" in err for err in errors))

    def test_factory_reset_uses_defaults_when_no_state_exists(self) -> None:
        paths = _paths()
        paths.data_dir.mkdir(parents=True)

        errors = factory_reset(paths)

        self.assertEqual(errors, [])
        self.assertTrue(paths.data_dir.is_dir())

    def test_factory_reset_deletes_rotated_logs_in_subdirs(self) -> None:
        paths = _paths()
        paths.data_dir.mkdir(parents=True)
        paths.logs_dir.mkdir(parents=True)
        (paths.logs_dir / "activity.log").write_text("current")
        (paths.logs_dir / "activity-20250615.log").write_text("rotated1")
        (paths.logs_dir / "activity-20250616.log").write_text("rotated2")

        errors = factory_reset(paths)

        self.assertEqual(errors, [])
        remaining = list(paths.logs_dir.iterdir()) if paths.logs_dir.exists() else []
        self.assertEqual(remaining, [])

    def test_factory_reset_deletes_legacy_queue_files(self) -> None:
        paths = _paths()
        paths.data_dir.mkdir(parents=True)
        (paths.data_dir / "queue.json").write_text('[{"url": "old"}]')
        (paths.data_dir / "queue.json.migrated").write_text("migrated")

        errors = factory_reset(paths)

        self.assertEqual(errors, [])
        self.assertFalse((paths.data_dir / "queue.json").exists())
        self.assertFalse((paths.data_dir / "queue.json.migrated").exists())

    def test_factory_reset_deletes_settings_and_cookies(self) -> None:
        paths = _paths()
        paths.data_dir.mkdir(parents=True)
        paths.settings_file.write_text(json.dumps({"preset": "audio-mp3"}))
        paths.cookies_file.write_text("# Netscape HTTP Cookie File\n.example.com\tTRUE\t/\tFALSE\t0\tname\tvalue")

        errors = factory_reset(paths)

        self.assertEqual(errors, [])
        self.assertFalse(paths.cookies_file.exists())
        settings = load_settings(paths)
        self.assertEqual(settings.preset, "best-video")

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
        deno_dir=root / "data" / "tools" / "deno",
        deno_executable=root / "data" / "tools" / "deno" / "deno.exe",
        download_dir=root / "downloads" / "youtube-download-helper",
    )


if __name__ == "__main__":
    unittest.main()
