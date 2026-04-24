from __future__ import annotations

import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

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
    def test_installing_percent_updates_progress_bar(self) -> None:
        app = _app_with_status_vars()

        app._set_status("installing", "Downloading ffmpeg 62%")  # noqa: SLF001

        self.assertEqual(app.status_var.value, "Downloading ffmpeg 62%")
        self.assertEqual(app.progress_var.value, 62)

    def test_installing_without_percent_keeps_coarse_progress(self) -> None:
        app = _app_with_status_vars()

        app._set_status("installing", "Downloading ffmpeg 18.4 MB")  # noqa: SLF001

        self.assertEqual(app.status_var.value, "Downloading ffmpeg 18.4 MB")
        self.assertEqual(app.progress_var.value, 5)

    def test_preset_mapping_uses_current_language_labels(self) -> None:
        app = YtDlpHelperApp.__new__(YtDlpHelperApp)
        app.language = "tr"
        app.preset_var = FakeVar()
        app.preset_combo = FakeWidget("Ses MP3")

        app._on_preset_changed(None)  # noqa: SLF001

        self.assertEqual(app.preset_var.value, "audio-mp3")

    def test_worker_status_messages_localize_at_ui_boundary(self) -> None:
        app = YtDlpHelperApp.__new__(YtDlpHelperApp)
        app.language = "tr"

        self.assertEqual(
            app._localized_worker_status("Resolving video information"),  # noqa: SLF001
            "Video bilgileri alınıyor",
        )
        self.assertEqual(
            app._localized_worker_status("Downloading ffmpeg 42%"),  # noqa: SLF001
            "ffmpeg indiriliyor 42%",
        )

    def test_settings_save_persists_language_and_refreshes_visible_ui(self) -> None:
        app = _app_with_localized_widgets()
        dialog = FakeDialog()

        with patch("ytdlp_helper.app.save_settings") as save_settings:
            app._save_settings_dialog(dialog, "Turkish", [("Turkish", "tr"), ("English", "en")])  # noqa: SLF001

        self.assertTrue(dialog.destroyed)
        self.assertEqual(app.language, "tr")
        saved_settings = save_settings.call_args.args[1]
        self.assertEqual(saved_settings.language, "tr")
        self.assertEqual(app.label_widgets["field.preset"].options["text"], "Ön Ayar")
        self.assertEqual(app.button_widgets["button.download"].options["text"], "İndir")
        self.assertEqual(app.button_widgets["button.download_playlist"].options["text"], "Oynatma Listesini İndir")
        self.assertEqual(app.preset_combo.options["values"][0], "En İyi Video")
        self.assertEqual(app.preset_label_var.value, "Ses M4A")
        self.assertEqual(app.archive_status_var.value, "Kontrol edilmedi")
        self.assertEqual(app.status_var.value, "Hazır")

    def test_action_state_applies_to_both_download_buttons(self) -> None:
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

        app._set_action_buttons_state("normal")  # noqa: SLF001

        self.assertTrue(app.actions_enabled)
        self.assertEqual(app.download_button.options["state"], "normal")
        self.assertEqual(app.download_playlist_button.options["state"], "normal")


def _app_with_status_vars() -> YtDlpHelperApp:
    app = YtDlpHelperApp.__new__(YtDlpHelperApp)
    app.language = "en"
    app.status_var = FakeVar()
    app.speed_var = FakeVar()
    app.progress_var = FakeVar()
    return app


def _app_with_localized_widgets() -> YtDlpHelperApp:
    app = YtDlpHelperApp.__new__(YtDlpHelperApp)
    app.language = "en"
    app.root = FakeRoot()
    app.paths = _paths()
    app.preset_var = FakeVar("audio-m4a")
    app.preset_label_var = FakeVar("Audio M4A")
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
