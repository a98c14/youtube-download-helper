from __future__ import annotations

import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from ytdlp_helper import __version__
from ytdlp_helper.app import YtDlpHelperApp
from ytdlp_helper.config import AppPaths


class FakeVar:
    def __init__(self, value: object | None = None) -> None:
        self.value = value

    def set(self, value: object) -> None:
        self.value = value

    def get(self) -> object:
        return self.value


class FakeWidget:
    def __init__(self, value: str = "") -> None:
        self.value = value
        self.options: dict[str, object] = {}

    def configure(self, **kwargs: object) -> None:
        self.options.update(kwargs)

    def get(self) -> str:
        return self.value


class FakeMenu:
    def __init__(self) -> None:
        self.entries: dict[int, dict[str, object]] = {}

    def entryconfigure(self, index: int, **kwargs: object) -> None:
        self.entries.setdefault(index, {}).update(kwargs)


class FakeRoot:
    def __init__(self) -> None:
        self.title_text = ""

    def title(self, text: str) -> None:
        self.title_text = text


class FakeDialog:
    def __init__(self) -> None:
        self.destroyed = False

    def destroy(self) -> None:
        self.destroyed = True


class AppStatusTests(unittest.TestCase):
    def test_preset_mapping_uses_current_language_labels(self) -> None:
        app = YtDlpHelperApp.__new__(YtDlpHelperApp)
        app.language = "tr"
        app.preset_var = FakeVar()
        app.preset_combo = FakeWidget("Ses MP3")

        app._on_preset_changed(None)  # noqa: SLF001

        self.assertEqual(app.preset_var.value, "audio-mp3")

    def test_settings_save_persists_language_and_refreshes_visible_ui(self) -> None:
        app = _app_with_localized_widgets()
        dialog = FakeDialog()
        new_download_dir = app.paths.data_dir.parent / "new-downloads"

        with patch("ytdlp_helper.app.save_settings") as save_settings:
            app._save_settings_dialog(  # noqa: SLF001
                dialog,
                "Turkish",
                [("Turkish", "tr"), ("English", "en")],
                str(new_download_dir),
                "%(upload_date)s - %(title)s.%(ext)s",
            )

        self.assertTrue(dialog.destroyed)
        self.assertEqual(app.language, "tr")
        saved_settings = save_settings.call_args.args[1]
        self.assertEqual(saved_settings.language, "tr")
        self.assertEqual(saved_settings.download_dir, str(new_download_dir))
        self.assertEqual(saved_settings.filename_template, "%(upload_date)s - %(title)s.%(ext)s")
        self.assertEqual(app.paths.download_dir, new_download_dir)
        self.assertEqual(app.download_folder_var.value, str(new_download_dir))
        self.assertEqual(app.filename_template_var.value, "%(upload_date)s - %(title)s.%(ext)s")
        self.assertEqual(app.label_widgets["field.preset"].options["text"], "Ön Ayar")
        self.assertEqual(app.button_widgets["button.download"].options["text"], "İndir")
        self.assertEqual(app.button_widgets["button.download_playlist"].options["text"], "Oynatma Listesini İndir")
        self.assertEqual(app.preset_combo.options["values"][0], "En İyi Video")
        self.assertEqual(app.preset_label_var.value, "Ses M4A")
        self.assertEqual(app.archive_status_var.value, "Kontrol edilmedi")
        self.assertEqual(app.status_var.value, "Hazır")
        self.assertEqual(app.help_menu.entries[1]["label"], "Hakkında")

    def test_settings_save_rejects_blank_filename_template(self) -> None:
        app = _app_with_localized_widgets()
        dialog = FakeDialog()

        with (
            patch("ytdlp_helper.app.save_settings") as save_settings,
            patch("ytdlp_helper.app.messagebox.showerror") as showerror,
        ):
            app._save_settings_dialog(  # noqa: SLF001
                dialog,
                "English",
                [("Turkish", "tr"), ("English", "en")],
                str(app.paths.download_dir),
                "  ",
            )

        self.assertFalse(dialog.destroyed)
        save_settings.assert_not_called()
        self.assertIn("Filename format required", showerror.call_args.args[0])

    def test_settings_save_rejects_filename_template_without_ext(self) -> None:
        app = _app_with_localized_widgets()
        dialog = FakeDialog()

        with (
            patch("ytdlp_helper.app.save_settings") as save_settings,
            patch("ytdlp_helper.app.messagebox.showerror") as showerror,
        ):
            app._save_settings_dialog(  # noqa: SLF001
                dialog,
                "English",
                [("Turkish", "tr"), ("English", "en")],
                str(app.paths.download_dir),
                "%(title)s",
            )

        self.assertFalse(dialog.destroyed)
        save_settings.assert_not_called()
        self.assertIn("Filename extension required", showerror.call_args.args[0])

    def test_settings_save_rejects_filename_template_with_path_separators(self) -> None:
        app = _app_with_localized_widgets()
        dialog = FakeDialog()

        with (
            patch("ytdlp_helper.app.save_settings") as save_settings,
            patch("ytdlp_helper.app.messagebox.showerror") as showerror,
        ):
            app._save_settings_dialog(  # noqa: SLF001
                dialog,
                "English",
                [("Turkish", "tr"), ("English", "en")],
                str(app.paths.download_dir),
                "nested/%(title)s.%(ext)s",
            )

        self.assertFalse(dialog.destroyed)
        save_settings.assert_not_called()
        self.assertIn("Filename format cannot include folders", showerror.call_args.args[0])

    def test_action_state_disables_download_buttons_and_update_menu_only(self) -> None:
        app = YtDlpHelperApp.__new__(YtDlpHelperApp)
        app.download_button = FakeWidget()
        app.download_playlist_button = FakeWidget()
        app.archive_check_button = FakeWidget()
        app.archive_clear_button = FakeWidget()
        app.archive_is_archived = True
        app.help_menu = FakeMenu()

        app._set_action_buttons_state("disabled")  # noqa: SLF001

        self.assertFalse(app.actions_enabled)
        self.assertEqual(app.download_button.options["state"], "disabled")
        self.assertEqual(app.download_playlist_button.options["state"], "disabled")
        self.assertEqual(app.help_menu.entries[0]["state"], "disabled")
        self.assertNotIn(1, app.help_menu.entries)

        app._set_action_buttons_state("normal")  # noqa: SLF001

        self.assertTrue(app.actions_enabled)
        self.assertEqual(app.download_button.options["state"], "normal")
        self.assertEqual(app.download_playlist_button.options["state"], "normal")
        self.assertEqual(app.help_menu.entries[0]["state"], "normal")
        self.assertNotIn(1, app.help_menu.entries)

    def test_about_message_includes_app_and_ytdlp_versions(self) -> None:
        app = YtDlpHelperApp.__new__(YtDlpHelperApp)
        app.language = "en"
        app.root = FakeRoot()
        app.paths = _paths()

        with (
            patch("ytdlp_helper.app.find_ytdlp_executable", return_value="C:/tools/yt-dlp.exe"),
            patch("ytdlp_helper.app.read_tool_version", return_value="2026.04.01"),
            patch("ytdlp_helper.app.messagebox.showinfo") as showinfo,
        ):
            app._show_about()  # noqa: SLF001

        title, message = showinfo.call_args.args
        self.assertEqual(title, "About YouTube Download Helper")
        self.assertIn(f"App version: {__version__}", message)
        self.assertIn("yt-dlp version: 2026.04.01", message)
        self.assertNotIn("ffmpeg", message)

    def test_about_message_reuses_cached_ytdlp_version(self) -> None:
        app = YtDlpHelperApp.__new__(YtDlpHelperApp)
        app.language = "en"
        app.root = FakeRoot()
        app.paths = _paths()

        with (
            patch("ytdlp_helper.app.find_ytdlp_executable", return_value="C:/tools/yt-dlp.exe"),
            patch("ytdlp_helper.app.read_tool_version", return_value="2026.04.01") as read_tool_version,
            patch("ytdlp_helper.app.messagebox.showinfo") as showinfo,
        ):
            app._show_about()  # noqa: SLF001
            app._show_about()  # noqa: SLF001

        read_tool_version.assert_called_once()
        self.assertEqual(showinfo.call_count, 2)

    def test_about_message_uses_fallback_for_missing_ytdlp_version(self) -> None:
        app = YtDlpHelperApp.__new__(YtDlpHelperApp)
        app.language = "en"
        app.root = FakeRoot()
        app.paths = _paths()

        with (
            patch("ytdlp_helper.app.find_ytdlp_executable", return_value=None),
            patch("ytdlp_helper.app.read_tool_version", return_value=None) as read_tool_version,
            patch("ytdlp_helper.app.messagebox.showinfo") as showinfo,
        ):
            app._show_about()  # noqa: SLF001
            app._show_about()  # noqa: SLF001

        message = showinfo.call_args.args[1]
        self.assertIn("yt-dlp version: Unavailable", message)
        self.assertNotIn("ffmpeg", message)
        read_tool_version.assert_not_called()

    def test_refresh_ytdlp_version_cache_updates_cached_value(self) -> None:
        app = YtDlpHelperApp.__new__(YtDlpHelperApp)
        app.paths = _paths()
        app.ytdlp_version_cache = "old"
        app.ytdlp_version_cache_ready = True

        with (
            patch("ytdlp_helper.app.find_ytdlp_executable", return_value="C:/tools/yt-dlp.exe"),
            patch("ytdlp_helper.app.read_tool_version", return_value="new"),
        ):
            app._refresh_ytdlp_version_cache()  # noqa: SLF001

        self.assertEqual(app.ytdlp_version_cache, "new")
        self.assertTrue(app.ytdlp_version_cache_ready)


def _app_with_localized_widgets() -> YtDlpHelperApp:
    app = YtDlpHelperApp.__new__(YtDlpHelperApp)
    app.language = "en"
    app.root = FakeRoot()
    app.paths = _paths()
    app.preset_var = FakeVar("audio-m4a")
    app.preset_label_var = FakeVar("Audio M4A")
    app.download_folder_var = FakeVar(str(app.paths.download_dir))
    app.filename_template_var = FakeVar("%(title)s [%(id)s].%(ext)s")
    app.archive_status_key = "archive.not_checked"
    app.archive_status_var = FakeVar("Not checked")
    app.cookie_status_var = FakeVar("No cookies saved")
    app.status_key = "status.ready"
    app.status_params = {}
    app.status_var = FakeVar("Ready")
    app.header_label = FakeWidget()
    app.subtitle_label = FakeWidget()
    app.label_widgets = {"field.preset": FakeWidget()}
    app.button_widgets = {"button.download": FakeWidget(), "button.download_playlist": FakeWidget()}
    app.menu_bar = FakeMenu()
    app.file_menu = FakeMenu()
    app.help_menu = FakeMenu()
    app.preset_combo = FakeWidget()
    app.log_window = None
    return app


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
        download_dir=root / "downloads",
    )


if __name__ == "__main__":
    unittest.main()
