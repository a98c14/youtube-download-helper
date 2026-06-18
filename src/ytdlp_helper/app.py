from __future__ import annotations

from dataclasses import replace
import os
from pathlib import Path
import subprocess
import threading
import tkinter as tk
from tkinter import filedialog, messagebox, simpledialog, ttk
from uuid import uuid4

from . import __version__
from .activity_log import ActivityLogStore
from .archive import (
    clear_archive_entry,
    is_archived,
    parse_youtube_video_id,
)
from .config import (
    Category,
    DEFAULT_FILENAME_TEMPLATE,
    MAX_QUEUE_CONCURRENCY,
    MIN_QUEUE_CONCURRENCY,
    Settings,
    ensure_app_dirs,
    find_ytdlp_executable,
    get_app_paths,
    load_settings,
    save_settings,
    settings_categories,
)
from .cookies import get_cookie_status, save_cookie_text
from .dependencies import RuntimeToolResolver, read_tool_version
from .download_queue import QueueItem, QueueRunner, QueueStore
from .database import Database
from .downloader import DownloadRequest, DownloadService
from .playlist_tracker import PlaylistChecker, canonical_playlist_url, parse_youtube_playlist_id
from .app_update import AppUpdateResult, start_restart_script
from .i18n import language_options, normalize_language, translate
from .update_service import UpdateService
from .worker_status import WorkerPhase, WorkerStatusPipeline, WorkerTask, WorkerUi, percent_from_message

PRESET_KEYS = [
    "best-video",
    "video-1080p",
    "video-720p",
    "video-480p",
    "audio-mp3",
    "audio-m4a",
]

QUEUE_FILTER_KEYS = [
    "all",
    "ongoing",
    "queued",
    "completed",
    "failed",
]


class YtDlpHelperApp:
    def __init__(self, root: tk.Tk) -> None:
        self.root = root
        self.root.title("YouTube Download Helper")
        self.root.geometry("920x620")
        self.root.minsize(820, 560)

        self.paths = get_app_paths()
        self.settings = load_settings(self.paths)
        imported_categories = settings_categories(self.settings, str(self.paths.download_dir))
        self.database = Database.for_data_dir(self.paths.data_dir)
        self.database_backup = self.database.initialize_with_recovery()
        self.database.import_categories(imported_categories)
        self.categories = self.database.categories() or imported_categories
        self.selected_category_id = self.settings.selected_category_id or self.categories[0].id
        selected_category = self._selected_category()
        self.language = normalize_language(self.settings.language)
        self.paths = replace(self.paths, download_dir=Path(selected_category.download_dir).expanduser())
        ensure_app_dirs(self.paths)
        self.runtime_tools = RuntimeToolResolver(self.paths)
        self.downloader = DownloadService(
            self.paths,
            self.settings.filename_template,
            self.settings.organize_by_channel,
            self._runtime_tool_resolver(),
        )
        self.update_service = UpdateService(self.paths, self._runtime_tool_resolver())
        self.activity_log = ActivityLogStore(self.paths)
        self.worker_pipeline = WorkerStatusPipeline(self, self._t, self.root.after)
        self.queue_store = QueueStore.for_paths(self.paths)
        self.queue_store.load()
        self.queue_runner = self._create_queue_runner()
        self.queue_user_paused = False
        self.ytdlp_version_cache: str | None = None
        self.ytdlp_version_cache_ready = False
        self.log_window: tk.Toplevel | None = None
        self.log_text: tk.Text | None = None
        self.copy_logs_button: ttk.Button | None = None
        self.label_widgets: dict[str, ttk.Label] = {}
        self.button_widgets: dict[str, ttk.Button] = {}

        self.url_var = tk.StringVar()
        self.archive_status_key = "archive.not_checked"
        self.archive_status_var = tk.StringVar(value=self._t(self.archive_status_key))
        self.archive_checked_video_id: str | None = None
        self.archive_is_archived = False
        self.actions_enabled = True
        self.preset_var = tk.StringVar(value=self.settings.preset)
        self.preset_label_var = tk.StringVar()
        self.category_label_var = tk.StringVar(value=selected_category.name)
        self.cookie_status_var = tk.StringVar(value=self._localized_cookie_status())
        self.status_key = "status.ready"
        self.status_var = tk.StringVar(value=self._t(self.status_key))
        self.speed_var = tk.StringVar(value=self._t("status.speed_empty"))
        self.download_folder_var = tk.StringVar(value=str(self.paths.download_dir))
        self.filename_template_var = tk.StringVar(value=self.settings.filename_template)
        self.queue_concurrency_var = tk.IntVar(value=self.settings.queue_concurrency)
        self.organize_by_channel_var = tk.BooleanVar(value=self.settings.organize_by_channel)
        self.queue_filter_var = tk.StringVar(value="all")
        self.queue_filter_label_var = tk.StringVar(value=self._queue_filter_label("all"))
        self.queue_summary_var = tk.StringVar()
        self.progress_var = tk.IntVar(value=0)
        self.queue_item_ids: dict[str, str] = {}
        self.tracker_check_running = False

        self._build_ui()
        self.url_var.trace_add("write", self._on_url_changed)
        self.root.after(150, self.worker_pipeline.poll)
        self.root.after(150, self._poll_queue_runner)
        self._refresh_queue_table()
        if self.database_backup:
            messagebox.showwarning(
                "Database recovered",
                f"The database could not be opened. A fresh database was created.\n\nBackup: {self.database_backup}",
                parent=self.root,
            )

    def _build_ui(self) -> None:
        self._build_menu()

        style = ttk.Style()
        style.configure("Header.TLabel", font=("Segoe UI Semibold", 18))
        style.configure("Hint.TLabel", foreground="#4b5563")

        container = ttk.Frame(self.root, padding=20)
        container.pack(fill="both", expand=True)
        container.columnconfigure(0, weight=1)

        self.header_label = ttk.Label(container, text=self._t("app.title"), style="Header.TLabel")
        self.header_label.grid(
            row=0, column=0, sticky="w"
        )
        self.subtitle_label = ttk.Label(
            container,
            text=self._t("header.subtitle"),
            style="Hint.TLabel",
        )
        self.subtitle_label.grid(row=1, column=0, sticky="w", pady=(4, 16))

        form = ttk.Frame(container)
        form.grid(row=2, column=0, sticky="nsew")
        form.columnconfigure(1, weight=1)

        self._form_label(form, "field.url").grid(row=0, column=0, sticky="w", padx=(0, 12), pady=(0, 12))
        ttk.Entry(form, textvariable=self.url_var).grid(row=0, column=1, sticky="ew", pady=(0, 12))

        self._form_label(form, "field.archive").grid(row=1, column=0, sticky="w", padx=(0, 12), pady=(0, 12))
        archive_row = ttk.Frame(form)
        archive_row.grid(row=1, column=1, sticky="ew", pady=(0, 12))
        archive_row.columnconfigure(0, weight=1)
        ttk.Label(archive_row, textvariable=self.archive_status_var).grid(row=0, column=0, sticky="w")
        self.archive_check_button = ttk.Button(
            archive_row,
            text=self._t("button.check"),
            command=self._check_archive_status,
        )
        self.button_widgets["button.check"] = self.archive_check_button
        self.archive_check_button.grid(row=0, column=1, sticky="e", padx=(10, 0))
        self.archive_clear_button = ttk.Button(
            archive_row,
            text=self._t("button.clear"),
            command=self._clear_archive_status,
            state="disabled",
        )
        self.button_widgets["button.clear"] = self.archive_clear_button
        self.archive_clear_button.grid(row=0, column=2, sticky="e", padx=(10, 0))

        self._form_label(form, "field.preset").grid(row=2, column=0, sticky="w", padx=(0, 12), pady=(0, 12))
        preset_combo = ttk.Combobox(
            form,
            textvariable=self.preset_label_var,
            state="readonly",
            values=self._preset_labels(),
        )
        preset_combo.grid(row=2, column=1, sticky="ew", pady=(0, 12))
        preset_combo.bind("<<ComboboxSelected>>", self._on_preset_changed)
        preset_combo.current(self._preset_index(self.settings.preset))
        self.preset_combo = preset_combo

        self._form_label(form, "field.category").grid(row=3, column=0, sticky="w", padx=(0, 12), pady=(0, 12))
        self.category_combo = ttk.Combobox(
            form,
            textvariable=self.category_label_var,
            state="readonly",
            values=[category.name for category in self.categories],
        )
        self.category_combo.grid(row=3, column=1, sticky="ew", pady=(0, 12))
        self.category_combo.bind("<<ComboboxSelected>>", self._on_category_changed)

        self._form_label(form, "field.cookies").grid(row=4, column=0, sticky="w", padx=(0, 12), pady=(0, 12))
        cookies_row = ttk.Frame(form)
        cookies_row.grid(row=4, column=1, sticky="ew", pady=(0, 12))
        cookies_row.columnconfigure(0, weight=1)
        ttk.Label(cookies_row, textvariable=self.cookie_status_var).grid(row=0, column=0, sticky="w")
        paste_cookies_button = ttk.Button(cookies_row, text=self._t("button.paste_cookies"), command=self._paste_cookies)
        self.button_widgets["button.paste_cookies"] = paste_cookies_button
        paste_cookies_button.grid(
            row=0, column=1, sticky="e", padx=(10, 0)
        )

        button_bar = ttk.Frame(container)
        button_bar.grid(row=3, column=0, sticky="ew", pady=(16, 12))

        self.download_button = ttk.Button(button_bar, text=self._t("button.download"), command=self._start_download)
        self.button_widgets["button.download"] = self.download_button
        self.download_button.pack(side="left", padx=(0, 10))
        self.download_playlist_button = ttk.Button(
            button_bar,
            text=self._t("button.download_playlist"),
            command=self._start_playlist_download,
        )
        self.button_widgets["button.download_playlist"] = self.download_playlist_button
        self.download_playlist_button.pack(side="left")
        self.open_downloads_icon = self._create_open_folder_icon()
        ttk.Button(
            button_bar,
            image=self.open_downloads_icon,
            command=self._open_downloads,
            width=3,
        ).pack(side="left", padx=(10, 0))

        queue_bar = ttk.Frame(container)
        queue_bar.grid(row=4, column=0, sticky="ew", pady=(0, 8))
        self.resume_button = ttk.Button(queue_bar, text=self._t("button.resume"), command=self._resume_queue)
        self.resume_button.pack(side="left", padx=(0, 8))
        self.pause_button = ttk.Button(queue_bar, text=self._t("button.pause"), command=self._pause_queue)
        self.pause_button.pack(side="left", padx=(0, 8))
        ttk.Label(queue_bar, text=self._t("field.concurrency")).pack(side="left", padx=(8, 4))
        ttk.Spinbox(
            queue_bar,
            from_=MIN_QUEUE_CONCURRENCY,
            to=MAX_QUEUE_CONCURRENCY,
            textvariable=self.queue_concurrency_var,
            width=4,
            command=self._persist_settings,
        ).pack(side="left", padx=(0, 12))
        filter_combo = ttk.Combobox(
            queue_bar,
            textvariable=self.queue_filter_label_var,
            values=self._queue_filter_labels(),
            state="readonly",
            width=12,
        )
        filter_combo.pack(side="left")
        filter_combo.bind("<<ComboboxSelected>>", self._on_queue_filter_changed)
        self.queue_filter_combo = filter_combo
        ttk.Label(queue_bar, textvariable=self.queue_summary_var).pack(side="right")

        columns = ("name", "category", "progress", "speed", "added", "status")
        self.queue_table = ttk.Treeview(container, columns=columns, show="headings", height=8, selectmode="browse")
        self.queue_table.grid(row=5, column=0, sticky="nsew", pady=(0, 8))
        container.rowconfigure(5, weight=1)
        self.queue_table.heading("name", text=self._t("queue.column.name"))
        self.queue_table.heading("category", text=self._t("queue.column.category"))
        self.queue_table.heading("progress", text=self._t("queue.column.progress"))
        self.queue_table.heading("speed", text=self._t("queue.column.speed"))
        self.queue_table.heading("added", text=self._t("queue.column.added"))
        self.queue_table.heading("status", text=self._t("queue.column.status"))
        self.queue_table.column("name", width=250, anchor="w")
        self.queue_table.column("category", width=110, anchor="w")
        self.queue_table.column("progress", width=80, anchor="center")
        self.queue_table.column("speed", width=100, anchor="center")
        self.queue_table.column("added", width=150, anchor="center")
        self.queue_table.column("status", width=90, anchor="center")
        self.queue_table.bind("<<TreeviewSelect>>", lambda _event: self._update_queue_action_state())
        self.queue_table.bind("<Double-1>", self._open_queue_item_file_from_event)
        self.queue_table.bind("<Button-3>", self._show_queue_context_menu)

        self.queue_context_menu = tk.Menu(self.root, tearoff=False)
        self.queue_context_menu.add_command(label=self._t("button.retry"), command=self._retry_selected_queue_item)
        self.queue_context_menu.add_command(label=self._t("button.remove"), command=self._remove_selected_queue_item)
        self.queue_context_menu.add_separator()
        self.queue_context_menu.add_command(label=self._t("button.move_up"), command=lambda: self._move_selected_queue_item(-1))
        self.queue_context_menu.add_command(label=self._t("button.move_down"), command=lambda: self._move_selected_queue_item(1))
        self.queue_context_menu.add_separator()
        self.queue_context_menu.add_command(label=self._t("button.open_folder"), command=self._open_selected_queue_folder)
        self.queue_context_menu.add_command(label=self._t("button.error_details"), command=self._show_selected_queue_error)

        queue_actions = ttk.Frame(container)
        queue_actions.grid(row=6, column=0, sticky="ew")
        self.retry_button = ttk.Button(queue_actions, text=self._t("button.retry"), command=self._retry_selected_queue_item)
        self.retry_button.pack(side="left", padx=(0, 8))
        self.remove_button = ttk.Button(queue_actions, text=self._t("button.remove"), command=self._remove_selected_queue_item)
        self.remove_button.pack(side="left", padx=(0, 8))
        self.move_up_button = ttk.Button(queue_actions, text=self._t("button.move_up"), command=lambda: self._move_selected_queue_item(-1))
        self.move_up_button.pack(side="left", padx=(0, 8))
        self.move_down_button = ttk.Button(queue_actions, text=self._t("button.move_down"), command=lambda: self._move_selected_queue_item(1))
        self.move_down_button.pack(side="left", padx=(0, 8))
        self.open_item_folder_button = ttk.Button(queue_actions, text=self._t("button.open_folder"), command=self._open_selected_queue_folder)
        self.open_item_folder_button.pack(side="left", padx=(0, 8))
        self.error_button = ttk.Button(queue_actions, text=self._t("button.error_details"), command=self._show_selected_queue_error)
        self.error_button.pack(side="left", padx=(0, 8))
        ttk.Button(queue_actions, text=self._t("button.clear_completed"), command=self._clear_completed_queue_items).pack(side="right")

    def _form_label(self, parent: ttk.Frame, key: str) -> ttk.Label:
        label = ttk.Label(parent, text=self._t(key))
        self.label_widgets[key] = label
        return label

    def _build_menu(self) -> None:
        menu_bar = tk.Menu(self.root)
        file_menu = tk.Menu(menu_bar, tearoff=False)
        file_menu.add_command(label=self._t("menu.settings"), command=self._show_settings)
        file_menu.add_command(label=self._t("menu.playlist_tracker"), command=self._show_playlist_tracker)
        file_menu.add_command(label=self._t("menu.download_history"), command=self._show_download_history)
        file_menu.add_command(label=self._t("menu.activity_log"), command=self._show_activity_log)
        help_menu = tk.Menu(menu_bar, tearoff=False)
        help_menu.add_command(label=self._t("menu.update"), command=self._start_update)
        help_menu.add_command(label=self._t("menu.about"), command=self._show_about)

        menu_bar.add_cascade(label=self._t("menu.file"), menu=file_menu)
        menu_bar.add_cascade(label=self._t("menu.help"), menu=help_menu)
        self.root.config(menu=menu_bar)
        self.menu_bar = menu_bar
        self.file_menu = file_menu
        self.help_menu = help_menu

    def _show_download_history(self) -> None:
        dialog = tk.Toplevel(self.root)
        dialog.title(self._t("menu.download_history"))
        dialog.geometry("900x420")
        columns = ("completed", "title", "category", "preset", "path")
        table = ttk.Treeview(dialog, columns=columns, show="headings")
        labels = ("Completed (UTC)", "Title", "Category", "Preset", "Output Path")
        for column, label in zip(columns, labels):
            table.heading(column, text=label)
        table.column("completed", width=155)
        table.column("title", width=210)
        table.column("category", width=110)
        table.column("preset", width=100)
        table.column("path", width=300)
        scrollbar = ttk.Scrollbar(dialog, orient="vertical", command=table.yview)
        table.configure(yscrollcommand=scrollbar.set)
        table.pack(side="left", fill="both", expand=True, padx=(12, 0), pady=12)
        scrollbar.pack(side="right", fill="y", padx=(0, 12), pady=12)
        for record in self.database.download_history():
            table.insert("", "end", values=(record.completed_at, record.title, record.category_name,
                                             record.preset, record.output_path))

    def _show_playlist_tracker(self) -> None:
        dialog = tk.Toplevel(self.root)
        dialog.title(self._t("menu.playlist_tracker"))
        dialog.geometry("860x430")
        columns = ("title", "state", "category", "preset", "attempt", "result")
        table = ttk.Treeview(dialog, columns=columns, show="headings", selectmode="browse")
        for column, label in zip(columns, ("Playlist", "State", "Category", "Preset", "Latest Check", "Result")):
            table.heading(column, text=label)
        table.column("title", width=220)
        table.column("state", width=80)
        table.column("category", width=120)
        table.column("preset", width=100)
        table.column("attempt", width=160)
        table.column("result", width=110)
        table.pack(fill="both", expand=True, padx=12, pady=(12, 6))

        def refresh() -> None:
            table.delete(*table.get_children())
            categories = {category.id: category.name for category in self.database.categories()}
            for tracker in self.database.trackers():
                table.insert("", "end", iid=str(tracker.id), values=(tracker.title or tracker.playlist_id,
                    "Active" if tracker.active else "Stopped", categories.get(tracker.category_id, "Default"),
                    tracker.preset, tracker.last_attempt_at, tracker.last_outcome.title()))

        def selected_id() -> int | None:
            selected = table.selection()
            return int(selected[0]) if selected else None

        def add() -> None:
            url = simpledialog.askstring(self._t("menu.playlist_tracker"), "YouTube playlist URL:", parent=dialog)
            if not url:
                return
            playlist_id = parse_youtube_playlist_id(url)
            if not playlist_id:
                messagebox.showerror(self._t("menu.playlist_tracker"), "Enter a YouTube URL containing a stable list ID.", parent=dialog)
                return
            try:
                self.database.add_tracker(playlist_id, canonical_playlist_url(playlist_id), playlist_id,
                                          self.preset_var.get(), self._selected_category().id)
            except Exception as exc:  # noqa: BLE001
                messagebox.showerror(self._t("menu.playlist_tracker"), str(exc), parent=dialog)
            refresh()

        def edit() -> None:
            tracker_id = selected_id()
            if tracker_id is not None:
                self.database.update_tracker(tracker_id, preset=self.preset_var.get(), category_id=self._selected_category().id)
                refresh()

        def toggle(active: bool) -> None:
            tracker_id = selected_id()
            if tracker_id is not None:
                self.database.set_tracker_active(tracker_id, active)
                refresh()

        def reset() -> None:
            tracker_id = selected_id()
            if tracker_id is not None and messagebox.askyesno("Reset Tracking State", "Offer all current entries again?", parent=dialog):
                self.database.reset_tracker(tracker_id)

        actions = ttk.Frame(dialog)
        actions.pack(fill="x", padx=12, pady=(0, 12))
        ttk.Button(actions, text="Add", command=add).pack(side="left")
        ttk.Button(actions, text="Edit Settings", command=edit).pack(side="left", padx=4)
        ttk.Button(actions, text="Stop Tracking", command=lambda: toggle(False)).pack(side="left", padx=4)
        ttk.Button(actions, text="Reactivate", command=lambda: toggle(True)).pack(side="left", padx=4)
        ttk.Button(actions, text="Reset Tracking State", command=reset).pack(side="left", padx=4)
        ttk.Button(actions, text="Check All", command=lambda: self._check_all_trackers(dialog, refresh)).pack(side="right")
        refresh()

    def _check_all_trackers(self, parent: tk.Misc, refresh: object) -> None:
        if self.tracker_check_running:
            messagebox.showinfo("Playlist Tracker", "A tracker check is already running.", parent=parent)
            return
        self.tracker_check_running = True

        def work() -> None:
            checker = PlaylistChecker(self.paths)
            counts: list[tuple[str, int, str]] = []
            for tracker in self.database.trackers():
                if not tracker.active:
                    continue
                try:
                    title, entries = checker.check(tracker.url)
                    self.database.record_playlist_check(tracker.id, entries)
                    counts.append((title or tracker.title or tracker.playlist_id, len(entries), ""))
                except Exception as exc:  # noqa: BLE001
                    self.database.record_playlist_check(tracker.id, None, str(exc))
                    counts.append((tracker.title or tracker.playlist_id, 0, str(exc)))
            self.root.after(0, lambda: finish(counts))

        def finish(counts: list[tuple[str, int, str]]) -> None:
            self.tracker_check_running = False
            refresh()  # type: ignore[operator]
            candidates = self.database.pending_candidates()
            summary = "\n".join(f"{name}: {'Failed - ' + error if error else str(count) + ' current'}" for name, count, error in counts)
            if not candidates:
                messagebox.showinfo("Playlist Tracker", summary + "\n\nNo pending entries.", parent=parent)
                return
            accepted = messagebox.askyesno("Playlist Tracker", summary + f"\n\nAdd {len(candidates)} pending entries to the queue?", parent=parent)
            if accepted:
                trackers = {tracker.id: tracker for tracker in self.database.trackers()}
                categories = {category.id: category for category in self.database.categories()}
                rows = []
                for candidate in candidates:
                    tracker = trackers[candidate.playlist_id]
                    category = categories[tracker.category_id]
                    template = self.filename_template_var.get().strip() or DEFAULT_FILENAME_TEMPLATE
                    if candidate.position is not None:
                        template = f"{candidate.position} - {template}"
                    rows.append({"url": f"https://www.youtube.com/watch?v={candidate.video_id}", "preset": tracker.preset,
                                 "download_dir": category.download_dir, "filename_template": template,
                                 "category_id": category.id, "category_name": category.name, "source_type": "tracker",
                                 "playlist_id": tracker.playlist_id, "playlist_position": candidate.position})
                self.queue_store.add_many(rows, [candidate.entry_id for candidate in candidates])
                self.queue_runner.notify_queue_changed()
                self._refresh_queue_table()
            else:
                self.database.decide_entries((candidate.entry_id for candidate in candidates), "dismissed")

        threading.Thread(target=work, daemon=True).start()

    def _show_settings(self) -> None:
        dialog = tk.Toplevel(self.root)
        dialog.title(self._t("settings.title"))
        dialog.geometry("760x560")
        dialog.resizable(False, False)
        dialog.transient(self.root)
        dialog.grab_set()

        selected_language = tk.StringVar()
        language_pairs = language_options(self.language)
        language_labels = [label for label, _ in language_pairs]
        selected_language.set(self._language_label_for_code(language_pairs, self.language))
        selected_filename_template = tk.StringVar(value=self.filename_template_var.get())
        selected_queue_concurrency = tk.IntVar(value=int(self.queue_concurrency_var.get()))
        selected_organize_by_channel = tk.BooleanVar(value=bool(self.organize_by_channel_var.get()))
        working_categories = list(self.categories)
        working_selected_id = [self.selected_category_id]

        frame = ttk.Frame(dialog, padding=16)
        frame.grid(row=0, column=0, sticky="nsew")
        frame.columnconfigure(1, weight=1)
        ttk.Label(frame, text=self._t("settings.language")).grid(
            row=0, column=0, sticky="w", padx=(0, 12), pady=(0, 12)
        )
        language_combo = ttk.Combobox(
            frame,
            textvariable=selected_language,
            values=language_labels,
            state="readonly",
            width=22,
        )
        language_combo.grid(row=0, column=1, sticky="ew", pady=(0, 12))

        categories_frame = ttk.LabelFrame(frame, text=self._t("settings.categories"), padding=12)
        categories_frame.grid(row=1, column=0, columnspan=2, sticky="nsew", pady=(0, 12))
        categories_frame.columnconfigure(1, weight=1)
        category_list = tk.Listbox(categories_frame, height=9, width=24, exportselection=False)
        category_list.grid(row=0, column=0, rowspan=4, sticky="ns", padx=(0, 12))
        category_name = tk.StringVar()
        category_folder = tk.StringVar()
        active_index = [0]
        refreshing = [False]

        ttk.Label(categories_frame, text=self._t("categories.name")).grid(row=0, column=1, sticky="w")
        ttk.Entry(categories_frame, textvariable=category_name).grid(row=1, column=1, sticky="ew", pady=(2, 10))
        ttk.Label(categories_frame, text=self._t("categories.folder")).grid(row=2, column=1, sticky="w")
        category_folder_row = ttk.Frame(categories_frame)
        category_folder_row.grid(row=3, column=1, sticky="ew")
        category_folder_row.columnconfigure(0, weight=1)
        ttk.Entry(category_folder_row, textvariable=category_folder).grid(row=0, column=0, sticky="ew")
        ttk.Button(
            category_folder_row,
            text=self._t("button.browse"),
            command=lambda: self._choose_settings_download_folder(category_folder, dialog),
        ).grid(row=0, column=1, padx=(8, 0))

        def store_active_category() -> None:
            if not working_categories:
                return
            index = active_index[0]
            existing = working_categories[index]
            working_categories[index] = Category(
                existing.id,
                category_name.get().strip(),
                category_folder.get().strip(),
            )
            refreshing[0] = True
            category_list.delete(index)
            category_list.insert(index, working_categories[index].name)
            category_list.selection_set(index)
            refreshing[0] = False

        def show_category(index: int) -> None:
            active_index[0] = max(0, min(index, len(working_categories) - 1))
            category = working_categories[active_index[0]]
            category_name.set(category.name)
            category_folder.set(category.download_dir)

        def refresh_categories(index: int) -> None:
            refreshing[0] = True
            category_list.delete(0, "end")
            for category in working_categories:
                category_list.insert("end", category.name)
            index = max(0, min(index, len(working_categories) - 1))
            category_list.selection_set(index)
            refreshing[0] = False
            show_category(index)

        def select_category(_event: object = None) -> None:
            if refreshing[0]:
                return
            selection = category_list.curselection()
            if not selection:
                return
            new_index = selection[0]
            if new_index != active_index[0]:
                store_active_category()
                refreshing[0] = True
                category_list.selection_clear(0, "end")
                category_list.selection_set(new_index)
                refreshing[0] = False
                show_category(new_index)

        def add_category() -> None:
            store_active_category()
            working_categories.append(
                Category(str(uuid4()), self._t("categories.new_name"), str(Path.home() / "Downloads"))
            )
            refresh_categories(len(working_categories) - 1)

        def remove_category() -> None:
            if len(working_categories) == 1:
                messagebox.showerror(
                    self._t("dialog.category_required.title"),
                    self._t("dialog.category_required.message"),
                    parent=dialog,
                )
                return
            if working_categories[active_index[0]].id == "default":
                messagebox.showerror(
                    self._t("dialog.category_required.title"),
                    "The Default Category cannot be deleted.",
                    parent=dialog,
                )
                return
            removed = working_categories.pop(active_index[0])
            if removed.id == working_selected_id[0]:
                working_selected_id[0] = working_categories[0].id
            refresh_categories(active_index[0])

        category_list.bind("<<ListboxSelect>>", select_category)
        category_actions = ttk.Frame(categories_frame)
        category_actions.grid(row=4, column=0, columnspan=2, sticky="w", pady=(12, 0))
        ttk.Button(category_actions, text=self._t("button.add"), command=add_category).pack(side="left")
        ttk.Button(category_actions, text=self._t("button.remove"), command=remove_category).pack(
            side="left", padx=(8, 0)
        )
        initial_category_index = next(
            (index for index, category in enumerate(working_categories) if category.id == working_selected_id[0]),
            0,
        )
        refresh_categories(initial_category_index)

        ttk.Label(frame, text=self._t("settings.filename_format")).grid(
            row=2, column=0, sticky="w", padx=(0, 12), pady=(0, 12)
        )
        ttk.Entry(frame, textvariable=selected_filename_template, width=46).grid(
            row=2, column=1, sticky="ew", pady=(0, 12)
        )

        ttk.Label(frame, text=self._t("field.concurrency")).grid(
            row=3, column=0, sticky="w", padx=(0, 12), pady=(0, 12)
        )
        ttk.Spinbox(
            frame,
            from_=MIN_QUEUE_CONCURRENCY,
            to=MAX_QUEUE_CONCURRENCY,
            textvariable=selected_queue_concurrency,
            width=6,
        ).grid(row=3, column=1, sticky="w", pady=(0, 12))

        ttk.Checkbutton(
            frame,
            text=self._t("settings.organize_by_channel"),
            variable=selected_organize_by_channel,
        ).grid(row=4, column=0, columnspan=2, sticky="w", pady=(0, 12))

        def save_dialog() -> None:
            store_active_category()
            self._save_settings_dialog(
                dialog,
                selected_language.get(),
                language_pairs,
                filename_template=selected_filename_template.get(),
                queue_concurrency=selected_queue_concurrency.get(),
                organize_by_channel=selected_organize_by_channel.get(),
                categories=working_categories,
                selected_category_id=working_selected_id[0],
            )

        button_bar = ttk.Frame(frame)
        button_bar.grid(row=5, column=0, columnspan=2, sticky="e")
        ttk.Button(button_bar, text=self._t("button.cancel"), command=dialog.destroy).pack(
            side="right", padx=(8, 0)
        )
        ttk.Button(
            button_bar,
            text=self._t("button.save"),
            command=save_dialog,
        ).pack(side="right")

        language_combo.focus_set()
        dialog.wait_window()

    def _save_settings_dialog(
        self,
        dialog: tk.Toplevel,
        selected_label: str,
        language_pairs: list[tuple[str, str]],
        download_folder: str | None = None,
        filename_template: str | None = None,
        queue_concurrency: int | None = None,
        organize_by_channel: bool | None = None,
        categories: list[Category] | None = None,
        selected_category_id: str | None = None,
    ) -> None:
        selected_language = self._language_code_for_label(language_pairs, selected_label)
        if not selected_language:
            return

        validated_categories = None
        if categories is not None:
            validated_categories = self._validate_categories(categories, dialog)
            if not validated_categories:
                return
            selected_id = selected_category_id or validated_categories[0].id
            if not any(category.id == selected_id for category in validated_categories):
                selected_id = validated_categories[0].id
            selected_category = next(category for category in validated_categories if category.id == selected_id)
            download_dir = Path(selected_category.download_dir)
        else:
            download_dir = self._validate_download_folder(download_folder or self.download_folder_var.get(), dialog)
            if not download_dir:
                return

        validated_template = self._validate_filename_template(
            filename_template if filename_template is not None else self.filename_template_var.get(),
            dialog,
        )
        if not validated_template:
            return

        self.language = selected_language
        if validated_categories is not None:
            self.categories = validated_categories
            self.selected_category_id = selected_id
            if hasattr(self, "category_label_var"):
                self.category_label_var.set(selected_category.name)
            if hasattr(self, "category_combo"):
                self.category_combo.configure(values=[category.name for category in validated_categories])
        self.paths = replace(self.paths, download_dir=download_dir)
        self.download_folder_var.set(str(download_dir))
        self.filename_template_var.set(validated_template)
        self.queue_concurrency_var.set(self._validate_queue_concurrency(queue_concurrency))
        self.organize_by_channel_var.set(
            bool(self.organize_by_channel_var.get()) if organize_by_channel is None else bool(organize_by_channel)
        )
        self.downloader = DownloadService(
            self.paths,
            validated_template,
            bool(self.organize_by_channel_var.get()),
            self._runtime_tool_resolver(),
        )
        self.update_service = UpdateService(self.paths, self._runtime_tool_resolver())
        if not self.queue_runner.is_running:
            self.queue_runner = self._create_queue_runner()
        self._persist_settings()
        self._refresh_language()
        dialog.destroy()

    def _language_label_for_code(self, pairs: list[tuple[str, str]], code: str) -> str:
        for label, language_code in pairs:
            if language_code == code:
                return label
        return pairs[0][0]

    def _language_code_for_label(self, pairs: list[tuple[str, str]], label: str) -> str | None:
        for language_label, language_code in pairs:
            if language_label == label:
                return language_code
        return None

    def _validate_categories(self, categories: list[Category], parent: tk.Misc) -> list[Category] | None:
        if not categories:
            messagebox.showerror(self._t("dialog.category_required.title"), self._t("dialog.category_required.message"), parent=parent)
            return None
        validated: list[Category] = []
        for category in categories:
            if not category.name.strip():
                messagebox.showerror(self._t("dialog.category_name_required.title"), self._t("dialog.category_name_required.message"), parent=parent)
                return None
            folder = self._validate_download_folder(category.download_dir, parent)
            if not folder:
                return None
            validated.append(Category(category.id, category.name.strip(), str(folder)))
        return validated

    def _on_category_changed(self, _event: tk.Event | None) -> None:
        selected_name = self.category_label_var.get()
        for category in self.categories:
            if category.name == selected_name:
                self.selected_category_id = category.id
                self.download_folder_var.set(category.download_dir)
                self._persist_settings()
                return

    def _show_activity_log(self) -> None:
        if self.log_window and self.log_window.winfo_exists():
            self.log_window.lift()
            self.log_window.focus_set()
            return

        log_window = tk.Toplevel(self.root)
        log_window.title(self._t("menu.activity_log"))
        log_window.geometry("820x480")
        log_window.minsize(640, 360)
        log_window.protocol("WM_DELETE_WINDOW", self._close_activity_log)

        frame = ttk.Frame(log_window, padding=12)
        frame.pack(fill="both", expand=True)
        frame.columnconfigure(0, weight=1)
        frame.rowconfigure(0, weight=1)

        text = tk.Text(frame, wrap="word", state="disabled")
        scrollbar = ttk.Scrollbar(frame, orient="vertical", command=text.yview)
        text.configure(yscrollcommand=scrollbar.set)
        text.grid(row=0, column=0, sticky="nsew")
        scrollbar.grid(row=0, column=1, sticky="ns")

        button_bar = ttk.Frame(frame)
        button_bar.grid(row=1, column=0, columnspan=2, sticky="e", pady=(10, 0))
        copy_button = ttk.Button(
            button_bar,
            text=self._t("button.copy_logs"),
            command=self._copy_activity_log_to_clipboard,
        )
        copy_button.pack(side="right")

        self.log_window = log_window
        self.log_text = text
        self.copy_logs_button = copy_button
        self._reload_activity_log_window()

    def _show_about(self) -> None:
        ytdlp_version = self._cached_ytdlp_version()
        message = "\n".join(
            [
                self._t("about.app_version", version=__version__),
                self._t("about.ytdlp_version", version=ytdlp_version),
            ]
        )
        messagebox.showinfo(self._t("about.title"), message, parent=self.root)

    def _cached_ytdlp_version(self) -> str:
        if not getattr(self, "ytdlp_version_cache_ready", False):
            self._refresh_ytdlp_version_cache()
        version = getattr(self, "ytdlp_version_cache", None)
        return version or self._t("about.unavailable")

    def _refresh_ytdlp_version_cache(self) -> None:
        executable = find_ytdlp_executable(self.paths)
        self.ytdlp_version_cache = read_tool_version(Path(executable)) if executable else None
        self.ytdlp_version_cache_ready = True

    def _close_activity_log(self) -> None:
        if self.log_window and self.log_window.winfo_exists():
            self.log_window.destroy()
        self.log_window = None
        self.log_text = None
        self.copy_logs_button = None

    def _reload_activity_log_window(self) -> None:
        if not self.log_text:
            return

        lines = self.activity_log.read_current_session_lines()
        self.log_text.configure(state="normal")
        self.log_text.delete("1.0", "end")
        if lines:
            self.log_text.insert("end", "\n".join(lines) + "\n")
        self.log_text.see("end")
        self.log_text.configure(state="disabled")

    def _copy_activity_log_to_clipboard(self) -> None:
        if not self.log_text:
            return

        contents = self.log_text.get("1.0", "end-1c")
        self.root.clipboard_clear()
        self.root.clipboard_append(contents)

    def _create_open_folder_icon(self) -> tk.PhotoImage:
        icon = tk.PhotoImage(width=16, height=16)
        icon.put("#d9a441", to=(1, 5, 15, 14))
        icon.put("#f0c15d", to=(1, 7, 15, 14))
        icon.put("#b98220", to=(1, 5, 15, 6))
        icon.put("#d9a441", to=(2, 3, 8, 6))
        icon.put("#f7d37a", to=(2, 8, 14, 13))
        icon.put("#ffffff", to=(11, 4, 13, 6))
        icon.put("#4b5563", to=(12, 3, 13, 4))
        icon.put("#4b5563", to=(13, 4, 14, 5))
        icon.put("#4b5563", to=(12, 5, 13, 6))
        return icon

    def _on_preset_changed(self, _event: object) -> None:
        selected_label = self.preset_combo.get()
        for key in PRESET_KEYS:
            if self._preset_label(key) == selected_label:
                self.preset_var.set(key)
                return

    def _on_queue_filter_changed(self, _event: object) -> None:
        self.queue_filter_var.set(self._queue_filter_key_for_label(self.queue_filter_combo.get()))
        self._refresh_queue_table()

    def _on_url_changed(self, *_args: object) -> None:
        self.archive_checked_video_id = None
        self.archive_is_archived = False
        self._set_archive_status("archive.not_checked")
        self._update_archive_buttons_state()

    def _check_archive_status(self) -> None:
        video_id = parse_youtube_video_id(self.url_var.get())
        if not video_id:
            self.archive_checked_video_id = None
            self.archive_is_archived = False
            self._set_archive_status("archive.unsupported_video_url")
            self._update_archive_buttons_state()
            return

        self._set_archive_status_for_video(video_id)

    def _set_archive_status_for_video(self, video_id: str) -> None:
        self.archive_checked_video_id = video_id
        self.archive_is_archived = is_archived(self.paths.archive_file, video_id)
        self._set_archive_status("archive.archived" if self.archive_is_archived else "archive.not_archived")
        self._update_archive_buttons_state()

    def _clear_archive_status(self) -> None:
        video_id = self.archive_checked_video_id
        if not video_id or not self.archive_is_archived:
            return

        confirmed = messagebox.askyesno(
            self._t("dialog.clear_archive.title"),
            self._t("dialog.clear_archive.message"),
            parent=self.root,
        )
        if not confirmed:
            return

        removed_count = clear_archive_entry(self.paths.archive_file, video_id)
        self._set_archive_status_for_video(video_id)

        if removed_count:
            message = f"Cleared archive entry for YouTube video {video_id}"
        else:
            message = f"No archive entry found for YouTube video {video_id}"
        self._set_status("queued", message)
        self._append_log(message)

    def _start_download(self) -> None:
        self._start_download_request(playlist=False)

    def _start_playlist_download(self) -> None:
        self._start_download_request(playlist=True)

    def _start_download_request(self, playlist: bool) -> None:
        url = self.url_var.get().strip()
        if not url:
            self._set_status("failed", "Enter a YouTube video or playlist URL.")
            return
        if not url.lower().startswith(("http://", "https://")):
            self._set_status("failed", "Enter a valid URL starting with http:// or https://.")
            return

        category = self._selected_category()
        download_dir = self._validate_download_folder(category.download_dir, self.root)
        if not download_dir:
            return

        if self.queue_store.has_duplicate_url(url):
            confirmed = messagebox.askyesno(
                self._t("dialog.duplicate_url.title"),
                self._t("dialog.duplicate_url.message"),
                parent=self.root,
            )
            if not confirmed:
                return

        self._persist_settings()
        try:
            item = self.queue_store.add(
                url,
                self.preset_var.get(),
                playlist,
                str(download_dir),
                self.filename_template_var.get().strip() or DEFAULT_FILENAME_TEMPLATE,
                category.id,
                category.name,
            )
        except Exception as exc:  # noqa: BLE001
            self._set_status("failed", str(exc))
            self._append_log(str(exc))
            return

        status_key = "status.queue_item_added_paused" if self.queue_user_paused else "status.queue_item_added"
        self._set_status_key("queued", status_key)
        self._append_log(f"Queued download for {item.url}")
        self.url_var.set("")
        if self.queue_user_paused:
            self.queue_runner.notify_queue_changed()
        else:
            self.queue_runner.resume(self._validate_queue_concurrency(self.queue_concurrency_var.get()))
        self._refresh_queue_table()

    def _resume_queue(self) -> None:
        self._persist_settings()
        self.queue_user_paused = False
        self.queue_runner.resume(self._validate_queue_concurrency(self.queue_concurrency_var.get()))
        self._refresh_queue_table()

    def _pause_queue(self) -> None:
        self.queue_user_paused = True
        self.queue_runner.pause()
        self._refresh_queue_table()

    def _poll_queue_runner(self) -> None:
        events = self.queue_runner.poll_events()
        if events:
            self._refresh_queue_table()
        self.root.after(150, self._poll_queue_runner)

    def _selected_queue_item(self) -> QueueItem | None:
        selection = self.queue_table.selection()
        if not selection:
            return None
        item_id = self.queue_item_ids.get(selection[0])
        return self.queue_store.get(item_id) if item_id else None

    def _retry_selected_queue_item(self) -> None:
        item = self._selected_queue_item()
        if item and self.queue_store.retry(item.id):
            self.queue_runner.notify_queue_changed()
            self._refresh_queue_table()

    def _remove_selected_queue_item(self) -> None:
        item = self._selected_queue_item()
        if item and self.queue_store.remove(item.id):
            self._refresh_queue_table()

    def _move_selected_queue_item(self, direction: int) -> None:
        item = self._selected_queue_item()
        if item and self.queue_store.move(item.id, direction):
            self._refresh_queue_table(selected_id=item.id)

    def _clear_completed_queue_items(self) -> None:
        self.queue_store.clear_completed()
        self._refresh_queue_table()

    def _open_selected_queue_folder(self) -> None:
        item = self._selected_queue_item()
        if item:
            subprocess.Popen(["explorer.exe", item.download_dir])

    def _open_queue_item_file_from_event(self, event: tk.Event) -> None:
        row_id = self.queue_table.identify_row(event.y)
        if not row_id:
            return
        self.queue_table.selection_set(row_id)
        self._update_queue_action_state()
        item_id = self.queue_item_ids.get(row_id)
        item = self.queue_store.get(item_id) if item_id else None
        if item:
            self._open_queue_item_file(item)

    def _open_queue_item_file(self, item: QueueItem) -> None:
        if item.status != "completed":
            return
        if not item.output_path:
            self._show_open_file_error(self._t("dialog.open_file_missing_path.message"))
            return
        output_path = Path(item.output_path).expanduser()
        if not output_path.exists():
            self._show_open_file_error(self._t("dialog.open_file_missing.message", path=output_path))
            return
        try:
            if hasattr(os, "startfile"):
                os.startfile(output_path)  # type: ignore[attr-defined]
            else:
                subprocess.Popen(["open", str(output_path)])
        except OSError as exc:
            self._show_open_file_error(self._t("dialog.open_file_failed.message", error=exc))

    def _show_open_file_error(self, message: str) -> None:
        self._set_status("failed", message)
        self._append_log(message)
        messagebox.showerror(self._t("dialog.open_file_failed.title"), message, parent=self.root)

    def _show_selected_queue_error(self) -> None:
        item = self._selected_queue_item()
        if item and item.error:
            messagebox.showerror(self._t("dialog.queue_error.title"), item.error, parent=self.root)

    def _show_queue_context_menu(self, event: tk.Event) -> None:
        row_id = self.queue_table.identify_row(event.y)
        if row_id:
            self.queue_table.selection_set(row_id)
            self._update_queue_action_state()
            self.queue_context_menu.tk_popup(event.x_root, event.y_root)

    def _refresh_queue_table(self, selected_id: str | None = None) -> None:
        if not hasattr(self, "queue_table"):
            return

        if selected_id is None:
            selected = self._selected_queue_item()
            selected_id = selected.id if selected else None

        for row_id in self.queue_table.get_children():
            self.queue_table.delete(row_id)
        self.queue_item_ids.clear()

        filter_key = self.queue_filter_var.get()
        selected_row = ""
        for item in self.queue_store.items():
            if not self._queue_item_matches_filter(item, filter_key):
                continue
            row_id = self.queue_table.insert(
                "",
                "end",
                values=(
                    item.name or item.url,
                    item.category_name,
                    f"{item.progress}%" if item.progress is not None else "",
                    item.speed if item.status == "running" else "",
                    item.added_at[:19].replace("T", " "),
                    self._t(f"queue.status.{item.status}"),
                ),
            )
            self.queue_item_ids[row_id] = item.id
            if item.id == selected_id:
                selected_row = row_id

        if selected_row:
            self.queue_table.selection_set(selected_row)
        self._update_queue_summary()
        self._update_queue_action_state()

    def _queue_item_matches_filter(self, item: QueueItem, filter_key: str) -> bool:
        if filter_key == "ongoing":
            return item.status in {"queued", "running"}
        if filter_key in {"queued", "completed", "failed"}:
            return item.status == filter_key
        return True

    def _update_queue_summary(self) -> None:
        counts = {status: 0 for status in ("queued", "running", "completed", "failed", "skipped")}
        for item in self.queue_store.items():
            counts[item.status] += 1
        self.queue_summary_var.set(
            self._t(
                "queue.summary",
                queued=counts["queued"],
                running=counts["running"],
                completed=counts["completed"],
                failed=counts["failed"],
                skipped=counts["skipped"],
            )
        )

    def _update_queue_action_state(self) -> None:
        if not hasattr(self, "retry_button"):
            return
        item = self._selected_queue_item()
        has_item = item is not None
        is_running = bool(item and item.status == "running")
        is_failed_or_skipped = bool(item and item.status in {"failed", "skipped"})
        has_error = bool(item and item.error)
        self.retry_button.configure(state="normal" if is_failed_or_skipped else "disabled")
        self.remove_button.configure(state="normal" if has_item and not is_running else "disabled")
        self.move_up_button.configure(state="normal" if has_item and not is_running else "disabled")
        self.move_down_button.configure(state="normal" if has_item and not is_running else "disabled")
        self.open_item_folder_button.configure(state="normal" if has_item else "disabled")
        self.error_button.configure(state="normal" if has_error else "disabled")
        if hasattr(self, "queue_context_menu"):
            self.queue_context_menu.entryconfigure(0, state="normal" if is_failed_or_skipped else "disabled")
            self.queue_context_menu.entryconfigure(1, state="normal" if has_item and not is_running else "disabled")
            self.queue_context_menu.entryconfigure(3, state="normal" if has_item and not is_running else "disabled")
            self.queue_context_menu.entryconfigure(4, state="normal" if has_item and not is_running else "disabled")
            self.queue_context_menu.entryconfigure(6, state="normal" if has_item else "disabled")
            self.queue_context_menu.entryconfigure(7, state="normal" if has_error else "disabled")

    def _start_update(self) -> None:
        if self.worker_pipeline.is_busy or self.queue_runner.is_running:
            messagebox.showinfo(self._t("dialog.task_in_progress.title"), self._t("dialog.task_in_progress.message"))
            return

        self.worker_pipeline.start(
            WorkerTask[AppUpdateResult](
                kind="update",
                initial_status_key="status.updating_runtime_tools",
                initial_log="Starting update",
                run=lambda reporter: self.update_service.update(
                    reporter.log_callback,
                    reporter.status_callback,
                ),
                success=self._update_succeeded,
                error_title_key="dialog.update_failed.title",
            )
        )

    def _download_succeeded(self, _result: None, ui: WorkerUi) -> None:
        ui.show_status_key("completed", "status.download_completed")
        ui.info("dialog.download_finished.title", "status.download_completed", localized=True)

    def _update_succeeded(self, result: AppUpdateResult, ui: WorkerUi) -> None:
        ui.refresh_runtime_version_cache()
        ui.show_status("completed", result.message)
        ui.show_progress(100)
        ui.show_speed(None)
        if result.restart_ready and result.restart_script:
            if ui.confirm("dialog.restart_to_update.title", "dialog.restart_to_update.message"):
                ui.show_status_key("completed", "status.ready_to_restart")
                ui.restart(result.restart_script)
            else:
                ui.info("dialog.update_ready.title", "dialog.update_ready.message", localized=True)
        else:
            ui.info("dialog.update_finished.title", result.message)

    def set_busy(self, busy: bool) -> None:
        self._set_action_buttons_state("disabled" if busy else "normal")

    def show_status(self, phase: WorkerPhase, message: str) -> None:
        self.status_key = None
        self.status_var.set(message)

    def show_status_key(self, phase: WorkerPhase, key: str, **params: object) -> None:
        self._set_status_key(phase, key, **params)

    def show_speed(self, speed: str | None) -> None:
        self.speed_var.set(self._t("status.speed", speed=speed) if speed else self._t("status.speed_empty"))

    def show_progress(self, value: int) -> None:
        self.progress_var.set(value)

    def append_log(self, message: str) -> None:
        self._append_log(message)

    def info(self, title_key: str, message: str, *, localized: bool = False) -> None:
        messagebox.showinfo(self._t(title_key), self._t(message) if localized else message, parent=self.root)

    def error(self, title_key: str, message: str) -> None:
        messagebox.showerror(self._t(title_key), message, parent=self.root)

    def confirm(self, title_key: str, message_key: str) -> bool:
        return messagebox.askyesno(self._t(title_key), self._t(message_key), parent=self.root)

    def restart(self, script_path: Path) -> None:
        start_restart_script(script_path)
        self.root.destroy()

    def refresh_runtime_version_cache(self) -> None:
        self._refresh_ytdlp_version_cache()

    def _set_status(self, status: str, message: str) -> None:
        if status == "speed":
            self.speed_var.set(self._t("status.speed", speed=message) if message else self._t("status.speed_empty"))
            return

        self.status_key = None
        self.status_var.set(message)
        if status == "downloading":
            percent = percent_from_message(message)
            self.progress_var.set(percent if percent is not None else 10)
        elif status == "installing":
            percent = percent_from_message(message)
            self.progress_var.set(percent if percent is not None else 5)
            self.speed_var.set(self._t("status.speed_empty"))
        elif status == "postprocessing":
            self.progress_var.set(95)
            self.speed_var.set(self._t("status.speed_empty"))
        elif status == "completed":
            self.progress_var.set(100)
            self.speed_var.set(self._t("status.speed_empty"))
        elif status in {"failed", "skipped"}:
            self.progress_var.set(0)
            self.speed_var.set(self._t("status.speed_empty"))
        elif status in {"queued", "resolving", "installing"}:
            self.progress_var.set(5)
            self.speed_var.set(self._t("status.speed_empty"))

    def _set_status_key(self, status: str, key: str, **params: object) -> None:
        self.status_key = key
        self.status_params = params
        self._set_status(status, self._t(key, **params))
        self.status_key = key

    def _append_log(self, message: str) -> None:
        timestamped_line = self.activity_log.append(message)
        if not timestamped_line or not self.log_text:
            return
        self.log_text.configure(state="normal")
        self.log_text.insert("end", timestamped_line + "\n")
        self.log_text.see("end")
        self.log_text.configure(state="disabled")

    def _persist_settings(self) -> None:
        category = self._selected_category()
        categories = getattr(self, "categories", None) or [category]
        self.settings = Settings(
            preset=self.preset_var.get(),
            download_dir=category.download_dir,
            language=self.language,
            filename_template=self.filename_template_var.get().strip() or DEFAULT_FILENAME_TEMPLATE,
            queue_concurrency=self._validate_queue_concurrency(self.queue_concurrency_var.get()),
            organize_by_channel=bool(self.organize_by_channel_var.get()),
            categories=None if hasattr(self, "database") else list(categories),
            selected_category_id=category.id,
        )
        if hasattr(self, "database"):
            self.database.replace_categories(categories)
        save_settings(self.paths, self.settings)

    def _selected_category(self) -> Category:
        categories = getattr(self, "categories", None) or [
            Category("default", "Default", str(self.paths.download_dir))
        ]
        selected_id = getattr(self, "selected_category_id", "")
        for category in categories:
            if category.id == selected_id:
                return category
        self.selected_category_id = categories[0].id
        return categories[0]

    def _create_queue_runner(self) -> QueueRunner:
        return QueueRunner(
            self.queue_store,
            self.paths,
            self._append_log,
            organize_by_channel_provider=lambda: bool(self.settings.organize_by_channel),
            runtime_tools=self._runtime_tool_resolver(),
        )

    def _choose_settings_download_folder(self, folder_var: tk.StringVar, parent: tk.Toplevel) -> None:
        current_dir = self._download_folder_from_value(folder_var.get())
        initial_dir = current_dir if current_dir and current_dir.exists() else Path.home()
        selected_dir = filedialog.askdirectory(
            parent=parent,
            title=self._t("dialog.choose_downloads.title"),
            initialdir=str(initial_dir),
        )
        if selected_dir:
            folder_var.set(selected_dir)

    def _apply_download_folder(self) -> bool:
        download_dir = self._validate_download_folder(self.download_folder_var.get(), self.root)
        if not download_dir:
            return False

        self.paths = replace(self.paths, download_dir=download_dir)
        self.downloader = DownloadService(
            self.paths,
            self.filename_template_var.get(),
            bool(self.organize_by_channel_var.get()),
            self._runtime_tool_resolver(),
        )
        self.update_service = UpdateService(self.paths, self._runtime_tool_resolver())
        if not self.queue_runner.is_running:
            self.queue_runner = self._create_queue_runner()
        self.download_folder_var.set(str(download_dir))
        return True

    def _download_folder_from_value(self, value: str) -> Path | None:
        raw_path = value.strip()
        if not raw_path:
            return None
        return Path(raw_path).expanduser()

    def _runtime_tool_resolver(self) -> RuntimeToolResolver:
        resolver = getattr(self, "runtime_tools", None)
        if resolver is None:
            resolver = RuntimeToolResolver(self.paths)
            self.runtime_tools = resolver
        return resolver

    def _validate_download_folder(self, value: str, parent: tk.Misc) -> Path | None:
        download_dir = self._download_folder_from_value(value)
        if not download_dir:
            message = self._t("dialog.downloads_required.message")
            self._set_status("failed", message)
            self._append_log(message)
            messagebox.showerror(self._t("dialog.downloads_required.title"), message, parent=parent)
            return None

        try:
            download_dir.mkdir(parents=True, exist_ok=True)
        except OSError as exc:
            message = self._t("dialog.downloads_unavailable.message", error=exc)
            self._set_status("failed", message)
            self._append_log(message)
            messagebox.showerror(self._t("dialog.downloads_unavailable.title"), message, parent=parent)
            return None

        return download_dir

    def _validate_filename_template(self, value: str, parent: tk.Misc) -> str | None:
        filename_template = value.strip()
        if not filename_template:
            message = self._t("dialog.filename_format_required.message")
            messagebox.showerror(self._t("dialog.filename_format_required.title"), message, parent=parent)
            return None
        if "%(ext)s" not in filename_template:
            message = self._t("dialog.filename_format_ext_required.message")
            messagebox.showerror(self._t("dialog.filename_format_ext_required.title"), message, parent=parent)
            return None
        if "/" in filename_template or "\\" in filename_template:
            message = self._t("dialog.filename_format_no_folders.message")
            messagebox.showerror(self._t("dialog.filename_format_no_folders.title"), message, parent=parent)
            return None
        return filename_template

    def _validate_queue_concurrency(self, value: object) -> int:
        try:
            concurrency = int(value)
        except (TypeError, ValueError):
            return 1
        return max(MIN_QUEUE_CONCURRENCY, min(MAX_QUEUE_CONCURRENCY, concurrency))

    def _open_downloads(self) -> None:
        if self._apply_download_folder():
            self._persist_settings()
            subprocess.Popen(["explorer.exe", str(self.paths.download_dir)])

    def _paste_cookies(self) -> None:
        try:
            text = self.root.clipboard_get()
        except tk.TclError:
            message = self._t("message.clipboard_no_cookies")
            self._set_status("failed", message)
            self._append_log(message)
            return

        try:
            save_cookie_text(self.paths, text)
        except ValueError as exc:
            self._set_status("failed", str(exc))
            self._append_log(str(exc))
            return

        self.cookie_status_var.set(self._localized_cookie_status())
        self._set_status_key("queued", "status.cookies_saved")
        self._append_log(f"Saved cookies to {self.paths.cookies_file}")

    def _set_action_buttons_state(self, state: str) -> None:
        self.actions_enabled = state == "normal"
        self.download_button.configure(state=state)
        self.download_playlist_button.configure(state=state)
        self.help_menu.entryconfigure(0, state=state)
        self._update_archive_buttons_state()

    def _update_archive_buttons_state(self) -> None:
        if not hasattr(self, "archive_check_button"):
            return
        check_state = "normal" if self.actions_enabled else "disabled"
        clear_state = "normal" if self.actions_enabled and self.archive_is_archived else "disabled"
        self.archive_check_button.configure(state=check_state)
        self.archive_clear_button.configure(state=clear_state)

    def _refresh_language(self) -> None:
        self.root.title(self._t("app.title"))
        self.header_label.configure(text=self._t("app.title"))
        self.subtitle_label.configure(text=self._t("header.subtitle"))
        for key, label in self.label_widgets.items():
            label.configure(text=self._t(key))
        for key, button in self.button_widgets.items():
            button.configure(text=self._t(key))

        self.menu_bar.entryconfigure(0, label=self._t("menu.file"))
        self.menu_bar.entryconfigure(1, label=self._t("menu.help"))
        self.file_menu.entryconfigure(0, label=self._t("menu.settings"))
        self.file_menu.entryconfigure(1, label=self._t("menu.playlist_tracker"))
        self.file_menu.entryconfigure(2, label=self._t("menu.download_history"))
        self.file_menu.entryconfigure(3, label=self._t("menu.activity_log"))
        self.help_menu.entryconfigure(0, label=self._t("menu.update"))
        self.help_menu.entryconfigure(1, label=self._t("menu.about"))

        self.preset_combo.configure(values=self._preset_labels())
        self.preset_label_var.set(self._preset_label(self.preset_var.get()))
        if hasattr(self, "queue_filter_combo"):
            self.queue_filter_combo.configure(values=self._queue_filter_labels())
            self.queue_filter_label_var.set(self._queue_filter_label(str(self.queue_filter_var.get())))
        self.archive_status_var.set(self._t(self.archive_status_key))
        self.cookie_status_var.set(self._localized_cookie_status())
        if getattr(self, "status_key", None):
            self.status_var.set(self._t(self.status_key, **getattr(self, "status_params", {})))
        if hasattr(self, "queue_table"):
            self.queue_table.heading("name", text=self._t("queue.column.name"))
            self.queue_table.heading("category", text=self._t("queue.column.category"))
            self.queue_table.heading("progress", text=self._t("queue.column.progress"))
            self.queue_table.heading("speed", text=self._t("queue.column.speed"))
            self.queue_table.heading("added", text=self._t("queue.column.added"))
            self.queue_table.heading("status", text=self._t("queue.column.status"))
            if hasattr(self, "queue_context_menu"):
                self.queue_context_menu.entryconfigure(0, label=self._t("button.retry"))
                self.queue_context_menu.entryconfigure(1, label=self._t("button.remove"))
                self.queue_context_menu.entryconfigure(3, label=self._t("button.move_up"))
                self.queue_context_menu.entryconfigure(4, label=self._t("button.move_down"))
                self.queue_context_menu.entryconfigure(6, label=self._t("button.open_folder"))
                self.queue_context_menu.entryconfigure(7, label=self._t("button.error_details"))
            self._refresh_queue_table()
        if self.log_window and self.log_window.winfo_exists():
            self.log_window.title(self._t("menu.activity_log"))
            if self.copy_logs_button:
                self.copy_logs_button.configure(text=self._t("button.copy_logs"))

    def _set_archive_status(self, key: str) -> None:
        self.archive_status_key = key
        self.archive_status_var.set(self._t(key))

    def _preset_index(self, preset_key: str) -> int:
        for index, value in enumerate(PRESET_KEYS):
            if value == preset_key:
                return index
        return 0

    def _preset_labels(self) -> list[str]:
        return [self._preset_label(key) for key in PRESET_KEYS]

    def _preset_label(self, key: str) -> str:
        return self._t(f"preset.{key}")

    def _queue_filter_labels(self) -> list[str]:
        return [self._queue_filter_label(key) for key in QUEUE_FILTER_KEYS]

    def _queue_filter_label(self, key: str) -> str:
        return self._t(f"queue.filter.{key}")

    def _queue_filter_key_for_label(self, label: str) -> str:
        for key in QUEUE_FILTER_KEYS:
            if self._queue_filter_label(key) == label:
                return key
        return "all"

    def _localized_cookie_status(self) -> str:
        status = get_cookie_status(self.paths)
        if status == "No cookies saved":
            return self._t("cookies.none")
        if status.startswith("Saved "):
            return self._t("cookies.saved", timestamp=status.removeprefix("Saved "))
        return status

    def _t(self, key: str, **params: object) -> str:
        return translate(getattr(self, "language", "tr"), key, **params)


def main() -> None:
    root = tk.Tk()
    app = YtDlpHelperApp(root)
    root.mainloop()
