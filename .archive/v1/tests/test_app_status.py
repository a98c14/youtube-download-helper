from __future__ import annotations

from dataclasses import replace
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from ytdlp_helper import __version__
from ytdlp_helper.app import YtDlpHelperApp
from ytdlp_helper.config import AppPaths, Category, factory_reset
from ytdlp_helper.download_queue import QueueItem
from ytdlp_helper.i18n import translate


class FakeCategoryController:
    def validate_download_folder(self, value: str) -> Path | None:
        raw_path = value.strip()
        if not raw_path:
            return None
        return Path(raw_path).expanduser()

    def validate_filename_template(self, value: str) -> str | None:
        filename_template = value.strip()
        if not filename_template:
            return None
        if "%(ext)s" not in filename_template:
            return None
        if "/" in filename_template or "\\" in filename_template:
            return None
        return filename_template

    def validate_queue_concurrency(self, value: object) -> int:
        try:
            concurrency = int(value)
        except (TypeError, ValueError):
            return 1
        return max(1, min(4, concurrency))

    def validate_categories(self, categories: list[Category]) -> list[Category] | None:
        if not categories:
            return None
        validated: list[Category] = []
        for category in categories:
            if not category.name.strip():
                return None
            folder = self.validate_download_folder(category.download_dir)
            if not folder:
                return None
            validated.append(Category(category.id, category.name.strip(), str(folder)))
        return validated

    def language_code_for_label(self, pairs: list[tuple[str, str]], label: str) -> str | None:
        for language_label, language_code in pairs:
            if language_label == label:
                return language_code
        return None

    def language_label_for_code(self, pairs: list[tuple[str, str]], code: str) -> str:
        for label, language_code in pairs:
            if language_code == code:
                return label
        return pairs[0][0]

    def selected_category(self, categories: list[Category], selected_id: str) -> Category:
        for category in categories:
            if category.id == selected_id:
                return category
        return categories[0]

    def persist_settings(self, paths: object, settings: object, categories: object) -> None:
        pass

    def load_categories(self) -> list[Category]:
        return []


class FakeQueueController:
    def __init__(self, store: object | None = None, runner: object | None = None) -> None:
        self._store = store
        self._runner = runner
        self._items: list[QueueItem] = []

    def items(self) -> list[QueueItem]:
        return list(self._items)

    def get_item(self, item_id: str) -> QueueItem | None:
        for item in self._items:
            if item.id == item_id:
                return item
        return None

    def find_existing(self, media_id: str, preset: str, download_dir: str,
                      filename_template: str, organize_by_channel: bool,
                      playlist_id: str) -> QueueItem | None:
        return None

    def add_item(
        self, url: str, preset: str, download_dir: str,
        filename_template: str, category_id: str, category_name: str,
        **kwargs: object,
    ) -> QueueItem:
        item = QueueItem(
            id=f"item-{len(self._items) + 1}",
            url=url,
            preset=preset,
            download_dir=download_dir,
            filename_template=filename_template,
            added_at="2026-04-24T00:00:00+00:00",
            category_id=category_id,
            category_name=category_name,
        )
        self._items.append(item)
        if self._store and hasattr(self._store, 'add'):
            self._store.add(url, preset, download_dir, filename_template, True, category_id, category_name)
        return item

    def retry(self, item_id: str) -> bool:
        return False

    def remove(self, item_id: str) -> bool:
        return False

    def move(self, item_id: str, direction: int) -> bool:
        return False

    def clear_completed(self) -> None:
        pass

    def items_matching_filter(self, filter_key: str) -> list[QueueItem]:
        return self._items

    def notify_changed(self) -> None:
        if self._runner and hasattr(self._runner, 'notify_queue_changed'):
            self._runner.notify_queue_changed()

    def resume(self, concurrency: int) -> None:
        if self._runner and hasattr(self._runner, 'resume'):
            self._runner.resume(concurrency)

    def pause(self) -> None:
        if self._runner and hasattr(self._runner, 'pause'):
            self._runner.pause()


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


class FakeText:
    def __init__(self, value: str) -> None:
        self.value = value

    def get(self, start: str, end: str) -> str:
        self.start = start
        self.end = end
        return self.value


class FakeWindow:
    def __init__(self, exists: bool = True) -> None:
        self.exists = exists
        self.title_text = ""

    def winfo_exists(self) -> bool:
        return self.exists

    def title(self, text: str) -> None:
        self.title_text = text


class FakeMenu:
    def __init__(self) -> None:
        self.entries: dict[int, dict[str, object]] = {}

    def entryconfigure(self, index: int, **kwargs: object) -> None:
        self.entries.setdefault(index, {}).update(kwargs)


class FakeQueueTable:
    def __init__(self) -> None:
        self.rows: dict[str, tuple[object, ...]] = {}
        self.order: list[str] = []
        self.selected: tuple[str, ...] = ()
        self.next_id = 1
        self.deleted: list[str] = []

    def get_children(self) -> tuple[str, ...]:
        return tuple(self.order)

    def insert(self, parent: str, index: object, values: tuple[object, ...]) -> str:
        row_id = f"row-{self.next_id}"
        self.next_id += 1
        self.rows[row_id] = values
        if isinstance(index, int):
            self.order.insert(index, row_id)
        else:
            self.order.append(row_id)
        return row_id

    def item(self, row_id: str, **kwargs: object) -> None:
        if "values" in kwargs:
            self.rows[row_id] = kwargs["values"]  # type: ignore[assignment]

    def move(self, row_id: str, parent: str, index: int) -> None:
        self.order.remove(row_id)
        self.order.insert(index, row_id)

    def delete(self, row_id: str) -> None:
        self.deleted.append(row_id)
        self.order.remove(row_id)
        self.rows.pop(row_id, None)

    def selection(self) -> tuple[str, ...]:
        return self.selected

    def selection_set(self, row_id: str) -> None:
        self.selected = (row_id,)


class FakeRoot:
    def __init__(self) -> None:
        self.title_text = ""
        self.clipboard = "existing"

    def title(self, text: str) -> None:
        self.title_text = text

    def clipboard_clear(self) -> None:
        self.clipboard = ""

    def clipboard_append(self, text: str) -> None:
        self.clipboard += text


class FakeDialog:
    def __init__(self) -> None:
        self.destroyed = False

    def destroy(self) -> None:
        self.destroyed = True


class FakeQueueRunner:
    def __init__(self) -> None:
        self.resumed_with: list[int] = []
        self.notify_count = 0
        self.pause_count = 0
        self.is_running = False

    def resume(self, concurrency: int) -> None:
        self.resumed_with.append(concurrency)

    def notify_queue_changed(self) -> None:
        self.notify_count += 1

    def pause(self) -> None:
        self.pause_count += 1


class FakeQueueStore:
    def __init__(self) -> None:
        self._items: list[QueueItem] = []

    def items(self) -> list[QueueItem]:
        return list(self._items)

    def add(
        self,
        url: str,
        preset: str,
        download_dir: str,
        filename_template: str,
        organize_by_channel: bool = True,
        category_id: str = "default",
        category_name: str = "Default",
    ) -> QueueItem:
        item = QueueItem(
            id=f"item-{len(self._items) + 1}",
            url=url,
            preset=preset,
            download_dir=download_dir,
            filename_template=filename_template,
            organize_by_channel=organize_by_channel,
            added_at="2026-04-24T00:00:00+00:00",
            category_id=category_id,
            category_name=category_name,
        )
        self._items.append(item)
        return item


class AppStatusTests(unittest.TestCase):
    def test_preset_mapping_uses_current_language_labels(self) -> None:
        app = YtDlpHelperApp.__new__(YtDlpHelperApp)
        app.language = "tr"
        app.preset_var = FakeVar()
        app.preset_combo = FakeWidget("Ses MP3")

        app._on_preset_changed(None)

        self.assertEqual(app.preset_var.value, "audio-mp3")

    def test_settings_save_persists_language_and_refreshes_visible_ui(self) -> None:
        app = _app_with_localized_widgets()
        dialog = FakeDialog()
        new_download_dir = app.paths.data_dir.parent / "new-downloads"

        with patch("ytdlp_helper.app.save_settings") as save_settings:
            app._save_settings_dialog(
                dialog,
                "Turkish",
                [("Turkish", "tr"), ("English", "en")],
                str(new_download_dir),
                "%(upload_date)s - %(title)s.%(ext)s",
                organize_by_channel=False,
            )

        self.assertTrue(dialog.destroyed)
        self.assertEqual(app.language, "tr")
        saved_settings = save_settings.call_args.args[1]
        self.assertEqual(saved_settings.language, "tr")
        self.assertEqual(saved_settings.download_dir, str(new_download_dir))
        self.assertEqual(saved_settings.filename_template, "%(upload_date)s - %(title)s.%(ext)s")
        self.assertEqual(saved_settings.queue_concurrency, 1)
        self.assertFalse(saved_settings.organize_by_channel)
        self.assertEqual(app.paths.download_dir, new_download_dir)
        self.assertEqual(app.download_folder_var.value, str(new_download_dir))
        self.assertEqual(app.filename_template_var.value, "%(upload_date)s - %(title)s.%(ext)s")
        self.assertFalse(app.organize_by_channel_var.value)
        self.assertEqual(app.label_widgets["field.preset"].options["text"], "Ön Ayar")
        self.assertEqual(app.button_widgets["button.download"].options["text"], "İndir")
        self.assertEqual(app.button_widgets["button.download_playlist"].options["text"], "Oynatma Listesi İndir")
        self.assertEqual(app.preset_combo.options["values"][0], "En İyi Video")
        self.assertEqual(app.preset_label_var.value, "Ses M4A")
        self.assertEqual(app.status_var.value, "Hazır")
        self.assertEqual(app.help_menu.entries[1]["label"], "Hakkında")

    def test_settings_save_commits_category_edits_with_general_settings(self) -> None:
        app = _app_with_localized_widgets()
        dialog = FakeDialog()
        category_dir = app.paths.data_dir.parent / "work"
        categories = [Category("work", "Work", str(category_dir))]

        with patch("ytdlp_helper.app.save_settings") as save_settings:
            app._save_settings_dialog(
                dialog,
                "English",
                [("English", "en")],
                filename_template="%(title)s.%(ext)s",
                categories=categories,
                selected_category_id="work",
            )

        self.assertEqual(app.categories, categories)
        self.assertEqual(app.selected_category_id, "work")
        self.assertEqual(app.paths.download_dir, category_dir)
        self.assertEqual(save_settings.call_args.args[1].categories, categories)

    def test_settings_save_destroys_dialog_before_refresh_even_when_refresh_raises(self) -> None:
        app = _app_with_localized_widgets()
        dialog = FakeDialog()

        def refresh() -> None:
            self.assertTrue(dialog.destroyed)
            raise RuntimeError("refresh failed")

        app._refresh_language = refresh
        with patch("ytdlp_helper.app.save_settings"):
            with self.assertRaisesRegex(RuntimeError, "refresh failed"):
                app._save_settings_dialog(
                    dialog,
                    "English",
                    [("English", "en")],
                    str(app.paths.download_dir),
                    "%(title)s.%(ext)s",
                )

        self.assertTrue(dialog.destroyed)



    def test_refresh_language_updates_open_activity_log_copy_button(self) -> None:
        app = _app_with_localized_widgets()
        app.log_window = FakeWindow()
        app.copy_logs_button = FakeWidget()
        app.language = "tr"

        app._refresh_language()

        self.assertEqual(app.log_window.title_text, "Logs")
        self.assertEqual(app.copy_logs_button.options["text"], "Logları Kopyala")

    def test_refresh_language_updates_queue_filter_labels_without_changing_filter(self) -> None:
        app = _app_with_localized_widgets()
        app.queue_filter_var = FakeVar("ongoing")
        app.queue_filter_label_var = FakeVar("Ongoing")
        app.queue_filter_combo = FakeWidget("Ongoing")
        app.language = "tr"

        app._refresh_language()

        self.assertEqual(
            app.queue_filter_combo.options["values"],
            ["Tümü", "Devam Eden", "Kuyrukta", "Tamamlandı", "Başarısız"],
        )
        self.assertEqual(app.queue_filter_label_var.value, "Devam Eden")
        self.assertEqual(app.queue_filter_var.value, "ongoing")

    def test_queue_filter_selection_uses_internal_filter_key(self) -> None:
        app = _app_with_localized_widgets()
        app.language = "tr"
        app.queue_filter_var = FakeVar("all")
        app.queue_filter_combo = FakeWidget("Tamamlandı")
        refresh_calls = []
        app._refresh_queue_table = lambda: refresh_calls.append(True)

        app._on_queue_filter_changed(None)

        self.assertEqual(app.queue_filter_var.value, "completed")
        self.assertEqual(refresh_calls, [True])

    def test_refresh_queue_table_updates_existing_rows_without_recreating_them(self) -> None:
        app = YtDlpHelperApp.__new__(YtDlpHelperApp)
        app.language = "en"
        app.queue_table = FakeQueueTable()
        app.queue_item_ids = {}
        app.queue_filter_var = FakeVar("all")
        app.queue_summary_var = FakeVar()
        app.queue_state_var = FakeVar()
        app.queue_user_paused = False
        app.queue_controller = FakeQueueController()
        app._t = lambda key, **params: translate("en", key, **params)
        item = QueueItem(
            id="item-1",
            url="https://example.test/video",
            preset="best-video",
            download_dir="downloads",
            filename_template="%(title)s.%(ext)s",
            added_at="2026-04-24T00:00:00+00:00",
            status="running",
            name="Video",
            progress=10,
            speed="1MiB/s",
        )
        app.queue_controller._items = [item]

        app._refresh_queue_table()
        row_id = app.queue_table.get_children()[0]
        app.queue_controller._items = [replace(item, progress=42, speed="2MiB/s")]

        app._refresh_queue_table(selected_id=item.id)

        self.assertEqual(app.queue_table.get_children(), (row_id,))
        self.assertEqual(app.queue_table.deleted, [])
        self.assertEqual(app.queue_table.rows[row_id][2], "42%")
        self.assertEqual(app.queue_table.rows[row_id][3], "2MiB/s")
        self.assertEqual(app.queue_table.selection(), (row_id,))

    def test_copy_activity_log_copies_visible_log_contents(self) -> None:
        app = YtDlpHelperApp.__new__(YtDlpHelperApp)
        app.root = FakeRoot()
        app.log_text = FakeText("line one\nline two")

        app._copy_activity_log_to_clipboard()

        self.assertEqual(app.root.clipboard, "line one\nline two")
        self.assertEqual(app.log_text.start, "1.0")
        self.assertEqual(app.log_text.end, "end-1c")

    def test_settings_save_rejects_blank_filename_template(self) -> None:
        app = _app_with_localized_widgets()
        dialog = FakeDialog()

        with (
            patch("ytdlp_helper.app.save_settings") as save_settings,
            patch("ytdlp_helper.app.messagebox.showerror") as showerror,
        ):
            app._save_settings_dialog(
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
            app._save_settings_dialog(
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
            app._save_settings_dialog(
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
        app.help_menu = FakeMenu()
        app._m_help_update = 0

        app._set_action_buttons_state("disabled")

        self.assertFalse(app.actions_enabled)
        self.assertEqual(app.download_button.options["state"], "disabled")
        self.assertEqual(app.download_playlist_button.options["state"], "disabled")
        self.assertEqual(app.help_menu.entries[0]["state"], "disabled")
        self.assertNotIn(1, app.help_menu.entries)

        app._set_action_buttons_state("normal")

        self.assertTrue(app.actions_enabled)
        self.assertEqual(app.download_button.options["state"], "normal")
        self.assertEqual(app.download_playlist_button.options["state"], "normal")
        self.assertEqual(app.help_menu.entries[0]["state"], "normal")
        self.assertNotIn(1, app.help_menu.entries)

    def test_start_download_request_auto_resumes_initial_idle_queue(self) -> None:
        app = _app_for_start_download()

        app._start_download()

        self.assertEqual(len(app.queue_store.items()), 1)
        self.assertEqual(app.queue_runner.resumed_with, [2])
        self.assertEqual(app.queue_runner.notify_count, 0)

    def test_start_download_snapshots_selected_category(self) -> None:
        app = _app_for_start_download()
        category_dir = app.paths.data_dir.parent / "work"
        app.categories = [Category("work", "Work", str(category_dir))]
        app.selected_category_id = "work"

        app._start_download()

        item = app.queue_store.items()[0]
        self.assertEqual((item.category_id, item.category_name), ("work", "Work"))
        self.assertEqual(item.download_dir, str(category_dir))

    def test_start_download_reports_normal_queued_message(self) -> None:
        app = _app_for_start_download()

        app._start_download()

        self.assertEqual(app.status_var.value, "Queued, starting when possible")
        self.assertEqual(app.status_key, "status.queue_item_added")

    def test_start_download_reports_paused_queued_message(self) -> None:
        app = _app_for_start_download()
        app.queue_user_paused = True

        app._start_download()

        self.assertEqual(app.status_var.value, "Queued; resume to start")
        self.assertEqual(app.status_key, "status.queue_item_added_paused")

    def test_pause_and_resume_track_explicit_session_pause(self) -> None:
        app = _app_for_start_download()
        app.queue_user_paused = False

        app._pause_queue()

        self.assertTrue(app.queue_user_paused)
        self.assertEqual(app.queue_runner.pause_count, 1)

        app._resume_queue()

        self.assertFalse(app.queue_user_paused)
        self.assertEqual(app.queue_runner.resumed_with, [2])

    def test_queue_state_idle_when_empty_and_not_paused(self) -> None:
        app = _app_for_start_download()

        app._update_queue_state()

        self.assertIn("Idle", app.queue_state_var.value)

    def test_queue_state_paused_when_user_paused_and_empty(self) -> None:
        app = _app_for_start_download()
        app.queue_user_paused = True

        app._update_queue_state()

        self.assertIn("Paused", app.queue_state_var.value)

    def test_queue_state_waiting_when_queued_and_not_paused(self) -> None:
        app = _app_for_start_download()
        app.queue_controller._items = [_queue_item("queued", "")]
        app.queue_user_paused = False

        app._update_queue_state()

        self.assertIn("Waiting", app.queue_state_var.value)

    def test_queue_state_running_when_item_running(self) -> None:
        app = _app_for_start_download()
        app.queue_controller._items = [_queue_item("running", "")]
        app.queue_user_paused = False

        app._update_queue_state()

        self.assertIn("Running", app.queue_state_var.value)

    def test_queue_state_pausing_when_running_and_user_paused(self) -> None:
        app = _app_for_start_download()
        app.queue_controller._items = [_queue_item("running", "")]
        app.queue_user_paused = True

        app._update_queue_state()

        self.assertIn("Pausing", app.queue_state_var.value)

    def test_queue_state_updates_after_pause(self) -> None:
        app = _app_for_start_download()

        app._pause_queue()

        self.assertTrue(app.queue_user_paused)
        self.assertIn("Paused", app.queue_state_var.value)

    def test_queue_state_updates_after_resume(self) -> None:
        app = _app_for_start_download()
        app.queue_user_paused = True

        app._resume_queue()

        self.assertFalse(app.queue_user_paused)
        self.assertIn("Idle", app.queue_state_var.value)

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
            app._show_about()

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
            app._show_about()
            app._show_about()

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
            app._show_about()
            app._show_about()

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
            app._refresh_ytdlp_version_cache()

        self.assertEqual(app.ytdlp_version_cache, "new")
        self.assertTrue(app.ytdlp_version_cache_ready)

    def test_open_queue_item_file_launches_completed_output(self) -> None:
        app = _app_for_open_file()
        output_path = app.paths.download_dir / "video.mp4"
        output_path.parent.mkdir(parents=True)
        output_path.write_text("media", encoding="utf-8")
        item = _queue_item(status="completed", output_path=str(output_path))

        with patch("ytdlp_helper.app.os.startfile", create=True) as startfile:
            app._open_queue_item_file(item)

        startfile.assert_called_once_with(output_path)

    def test_open_queue_item_file_ignores_incomplete_item(self) -> None:
        app = _app_for_open_file()
        output_path = app.paths.download_dir / "video.mp4"
        output_path.parent.mkdir(parents=True)
        output_path.write_text("media", encoding="utf-8")
        item = _queue_item(status="running", output_path=str(output_path))

        with patch("ytdlp_helper.app.os.startfile", create=True) as startfile:
            app._open_queue_item_file(item)

        startfile.assert_not_called()

    def test_open_queue_item_file_reports_missing_path(self) -> None:
        app = _app_for_open_file()
        item = _queue_item(status="completed", output_path="")

        with patch("ytdlp_helper.app.messagebox.showerror") as showerror:
            app._open_queue_item_file(item)

        self.assertIn("does not have a saved file path", showerror.call_args.args[1])
        self.assertIn("does not have a saved file path", app.logs[0])


    def test_factory_reset_blocked_by_active_worker(self) -> None:
        app = _app_for_factory_reset()
        app.worker_pipeline.is_busy = True

        with patch("ytdlp_helper.app.messagebox.showinfo") as showinfo:
            app._factory_reset()

        showinfo.assert_called_once()
        self.assertIn("active", showinfo.call_args.args[1].lower())

    def test_factory_reset_blocked_by_active_queue_runner(self) -> None:
        app = _app_for_factory_reset()
        app.queue_runner.is_running = True

        with patch("ytdlp_helper.app.messagebox.showinfo") as showinfo:
            app._factory_reset()

        showinfo.assert_called_once()
        self.assertIn("active", showinfo.call_args.args[1].lower())

    def test_factory_reset_blocked_by_tracker_check(self) -> None:
        app = _app_for_factory_reset()
        app.tracker_check_running = True

        with patch("ytdlp_helper.app.messagebox.showinfo") as showinfo:
            app._factory_reset()

        showinfo.assert_called_once()
        self.assertIn("active", showinfo.call_args.args[1].lower())

    def test_factory_reset_allowed_with_inactive_queue(self) -> None:
        app = _app_for_factory_reset()
        app.queue_store._items = [
            _queue_item("queued", ""),
            _queue_item("completed", ""),
            _queue_item("failed", ""),
        ]
        app.queue_runner.is_running = False
        app.worker_pipeline.is_busy = False
        app.tracker_check_running = False

        confirm_calls = []
        info_calls = []

        def fake_confirm(title: str, message: str, **kwargs: object) -> bool:
            confirm_calls.append((title, message))
            return True

        def fake_info(title: str, message: str, **kwargs: object) -> None:
            info_calls.append((title, message))

        with (
            patch("ytdlp_helper.app.factory_reset", return_value=[]),
            patch("ytdlp_helper.app.messagebox.askyesno", fake_confirm),
            patch("ytdlp_helper.app.messagebox.showinfo", fake_info),
        ):
            app._factory_reset()

        self.assertEqual(len(confirm_calls), 1)
        self.assertIn("Factory Reset", confirm_calls[0][0])
        self.assertEqual(len(info_calls), 1)
        self.assertIn("completed", info_calls[0][1].lower())

    def test_factory_reset_cancellable_from_confirm_dialog(self) -> None:
        app = _app_for_factory_reset()

        with (
            patch("ytdlp_helper.app.messagebox.askyesno", return_value=False),
            patch("ytdlp_helper.app.factory_reset") as factory_reset_mock,
        ):
            app._factory_reset()

        factory_reset_mock.assert_not_called()

    def test_factory_reset_reports_failure_on_errors(self) -> None:
        app = _app_for_factory_reset()

        errors = ["Could not delete app.db: permission denied"]

        def fake_confirm(title: str, message: str, **kwargs: object) -> bool:
            return True

        with (
            patch("ytdlp_helper.app.factory_reset", return_value=errors),
            patch("ytdlp_helper.app.messagebox.askyesno", fake_confirm),
            patch("ytdlp_helper.app.messagebox.showerror") as showerror,
        ):
            app._factory_reset()

        showerror.assert_called_once()
        self.assertIn("permission denied", showerror.call_args.args[1])

    def test_factory_reset_reinitializes_state_after_success(self) -> None:
        app = _app_for_factory_reset()
        app.language = "en"

        def fake_confirm(title: str, message: str, **kwargs: object) -> bool:
            return True

        with (
            patch("ytdlp_helper.app.factory_reset", return_value=[]),
            patch("ytdlp_helper.app.messagebox.askyesno", fake_confirm),
            patch("ytdlp_helper.app.messagebox.showinfo"),
        ):
            app._factory_reset()

        self.assertEqual(app.status_key, "status.ready")
        self.assertEqual(app.status_var.value, "Ready")
        self.assertEqual(app.preset_var.value, "best-video")
        self.assertEqual(app.filename_template_var.value, "%(title)s.%(ext)s")
        self.assertEqual(app.queue_concurrency_var.value, 1)
        self.assertTrue(app.organize_by_channel_var.value)
        self.assertFalse(app.queue_user_paused)
        self.assertIsNone(app.log_window)

    def test_factory_reset_clears_queue_state(self) -> None:
        app = _app_for_factory_reset()
        app.queue_store._items = [_queue_item("completed", "downloads/video.mp4")]

        def fake_confirm(title: str, message: str, **kwargs: object) -> bool:
            return True

        with (
            patch("ytdlp_helper.app.factory_reset", return_value=[]),
            patch("ytdlp_helper.app.messagebox.askyesno", fake_confirm),
            patch("ytdlp_helper.app.messagebox.showinfo"),
        ):
            app._factory_reset()

        self.assertEqual(len(app.queue_store.items()), 0)

    def test_factory_reset_preserves_running_session_language(self) -> None:
        app = _app_for_factory_reset()
        app.language = "tr"

        def fake_confirm(title: str, message: str, **kwargs: object) -> bool:
            return True

        with (
            patch("ytdlp_helper.app.factory_reset", return_value=[]),
            patch("ytdlp_helper.app.messagebox.askyesno", fake_confirm),
            patch("ytdlp_helper.app.messagebox.showinfo"),
        ):
            app._factory_reset()

        self.assertEqual(app.language, "tr")

    def test_factory_reset_closes_activity_log_window(self) -> None:
        app = _app_for_factory_reset()
        app.log_window = FakeWindow()
        app.log_text = FakeText("content")

        def fake_confirm(title: str, message: str, **kwargs: object) -> bool:
            return True

        with (
            patch("ytdlp_helper.app.factory_reset", return_value=[]),
            patch("ytdlp_helper.app.messagebox.askyesno", fake_confirm),
            patch("ytdlp_helper.app.messagebox.showinfo"),
        ):
            app._factory_reset()

        self.assertIsNone(app.log_window)
        self.assertIsNone(app.log_text)

    def test_factory_reset_resets_cookie_status(self) -> None:
        app = _app_for_factory_reset()
        app.cookie_status_var.value = "Saved 2026-01-01"

        def fake_confirm(title: str, message: str, **kwargs: object) -> bool:
            return True

        with (
            patch("ytdlp_helper.app.factory_reset", return_value=[]),
            patch("ytdlp_helper.app.messagebox.askyesno", fake_confirm),
            patch("ytdlp_helper.app.messagebox.showinfo"),
        ):
            app._factory_reset()

        self.assertIn("No cookies", app.cookie_status_var.value)


def _app_for_factory_reset() -> YtDlpHelperApp:
    app = YtDlpHelperApp.__new__(YtDlpHelperApp)
    app.language = "en"
    app.root = FakeRoot()
    app.paths = _paths()
    app.paths.download_dir.mkdir(parents=True)
    app.paths.logs_dir.mkdir(parents=True)
    app.settings = SimpleNamespace(
        preset="best-video", language="en", filename_template="%(title)s.%(ext)s",
        queue_concurrency=1, organize_by_channel=True,
    )
    app.database = SimpleNamespace()
    app.database.categories = lambda: [Category("default", "Default", str(app.paths.download_dir))]
    app.database.initialize_with_recovery = lambda: None
    app.database.import_categories = lambda _cats: None
    app.database.replace_categories = lambda _cats: None
    app.categories = [Category("default", "Default", str(app.paths.download_dir))]
    app.selected_category_id = "default"
    app.update_service = SimpleNamespace()
    app.activity_log = SimpleNamespace()
    app.worker_pipeline = SimpleNamespace(is_busy=False)
    app.queue_store = FakeQueueStore()
    app.queue_runner = FakeQueueRunner()
    app.queue_user_paused = False
    app.tracker_controller = SimpleNamespace(check_running=False)
    app.category_controller = FakeCategoryController()
    app.queue_controller = FakeQueueController()
    app.history_viewer = SimpleNamespace(get_history=lambda: [])
    app.tracker_check_running = False
    app.ytdlp_version_cache = None
    app.ytdlp_version_cache_ready = False
    app.log_window = None
    app.log_text = None
    app.copy_logs_button = None
    app.label_widgets = {}
    app.button_widgets = {}
    app.url_var = FakeVar("https://www.youtube.com/watch?v=old")
    app.actions_enabled = True
    app.preset_var = FakeVar("audio-mp3")
    app.preset_label_var = FakeVar("Audio MP3")
    app.category_label_var = FakeVar("Default")
    app.cookie_status_var = FakeVar("Saved 2026-01-01")
    app.status_key = "status.download_completed"
    app.status_var = FakeVar("Download completed")
    app.speed_var = FakeVar("Speed: 5 MB/s")
    app.download_folder_var = FakeVar(str(app.paths.download_dir))
    app.filename_template_var = FakeVar("%(upload_date)s - %(title)s.%(ext)s")
    app.queue_concurrency_var = FakeVar(3)
    app.organize_by_channel_var = FakeVar(False)
    app.queue_filter_var = FakeVar("completed")
    app.queue_filter_label_var = FakeVar("Completed")
    app.queue_summary_var = FakeVar()
    app.queue_state_var = FakeVar()
    app.progress_var = FakeVar(50)
    app.queue_item_ids = {}
    app._append_log = lambda _msg: None
    app._create_open_folder_icon = lambda: None
    app._runtime_tool_resolver = lambda: None
    app._create_queue_runner = lambda: FakeQueueRunner()
    app._selected_category = lambda: Category("default", "Default", str(app.paths.download_dir))
    app._t = lambda key, **params: translate("en", key, **params)
    app._preset_label = lambda key: translate("en", f"preset.{key}")
    app._queue_filter_label = lambda key: translate("en", f"queue.filter.{key}")
    app._localized_cookie_status = lambda: "No cookies saved"
    app._close_activity_log = lambda: setattr(app,
        "log_window", None) or setattr(app, "log_text", None)
    app._refresh_queue_table = lambda: None
    return app


def _app_with_localized_widgets() -> YtDlpHelperApp:
    app = YtDlpHelperApp.__new__(YtDlpHelperApp)
    app.language = "en"
    app.root = FakeRoot()
    app.paths = _paths()
    app.paths.download_dir.mkdir(parents=True)
    app.preset_var = FakeVar("audio-m4a")
    app.preset_label_var = FakeVar("Audio M4A")
    app.download_folder_var = FakeVar(str(app.paths.download_dir))
    app.filename_template_var = FakeVar("%(title)s [%(id)s].%(ext)s")
    app.queue_concurrency_var = FakeVar(1)
    app.organize_by_channel_var = FakeVar(True)
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
    app._m_file_settings = 0
    app._m_file_tracker = 1
    app._m_file_history = 2
    app._m_file_log = 3
    app._m_file_reset = 5
    app._m_help_update = 0
    app._m_help_about = 1
    app._ctx_retry_id = 0
    app._ctx_remove_id = 1
    app._ctx_up_id = 3
    app._ctx_down_id = 4
    app._ctx_folder_id = 6
    app._ctx_error_id = 7
    app.preset_combo = FakeWidget()
    app.log_window = None
    app.queue_runner = type("FakeQueueRunner", (), {"is_running": False})()
    app.queue_store = object()
    app.category_controller = FakeCategoryController()
    app.queue_controller = FakeQueueController()
    return app


def _app_for_start_download() -> YtDlpHelperApp:
    app = YtDlpHelperApp.__new__(YtDlpHelperApp)
    app.language = "en"
    app.root = FakeRoot()
    app.paths = _paths()
    app.paths.download_dir.mkdir(parents=True)
    app.url_var = FakeVar("https://www.youtube.com/watch?v=abc123")
    app.preset_var = FakeVar("best-video")
    app.filename_template_var = FakeVar("%(title)s [%(id)s].%(ext)s")
    app.queue_concurrency_var = FakeVar(2)
    app.organize_by_channel_var = FakeVar(True)
    app.status_var = FakeVar()
    app.speed_var = FakeVar()
    app.progress_var = FakeVar()
    app.status_key = "status.ready"
    app.status_params = {}
    app.queue_state_var = FakeVar()
    app.queue_user_paused = False
    app.queue_runner = FakeQueueRunner()
    app.queue_store = FakeQueueStore()
    app.category_controller = FakeCategoryController()
    app.queue_controller = FakeQueueController(store=app.queue_store, runner=app.queue_runner)
    app.logs = []
    app._append_log = app.logs.append
    app._persist_settings = lambda: None
    app._refresh_queue_table = lambda: None
    return app


def _app_for_open_file() -> YtDlpHelperApp:
    app = YtDlpHelperApp.__new__(YtDlpHelperApp)
    app.language = "en"
    app.root = FakeRoot()
    app.paths = _paths()
    app.status_var = FakeVar()
    app.speed_var = FakeVar()
    app.progress_var = FakeVar()
    app.logs = []
    app._append_log = app.logs.append
    return app


def _queue_item(status: str, output_path: str) -> QueueItem:
    return QueueItem(
        id="item",
        url="https://example.test/video",
        preset="best-video",
        download_dir="downloads",
        filename_template="%(title)s.%(ext)s",
        added_at="2026-04-24T00:00:00+00:00",
        status=status,
        output_path=output_path,
    )


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
