from __future__ import annotations

from dataclasses import replace
from pathlib import Path
import queue
import re
import subprocess
import threading
import tkinter as tk
from tkinter import filedialog, messagebox, ttk

from . import __version__
from .activity_log import ActivityLogStore
from .archive import (
    clear_archive_entry,
    is_archived,
    parse_youtube_video_id,
)
from .config import (
    Settings,
    ensure_app_dirs,
    find_ytdlp_executable,
    get_app_paths,
    load_settings,
    save_settings,
)
from .cookies import get_cookie_status, save_cookie_text
from .dependencies import read_tool_version
from .downloader import DownloadRequest, DownloadService
from .app_update import AppUpdateResult, start_restart_script
from .i18n import language_options, normalize_language, translate
from .update_service import UpdateService


STATUS_MESSAGE_KEYS = {
    "Updating runtime tools": "status.updating_runtime_tools",
    "Checking yt-dlp": "status.checking_ytdlp",
    "Checking ffmpeg": "status.checking_ffmpeg",
    "Installing yt-dlp": "status.installing_ytdlp",
    "Installing ffmpeg": "status.installing_ffmpeg",
    "Preparing download": "status.preparing_download",
    "Resolving video information": "status.resolving_video",
    "Resolving playlist": "status.resolving_playlist",
    "Finalizing file": "status.finalizing_file",
    "Already downloaded; skipped by archive": "status.archive_skipped",
    "Checking latest app release": "status.checking_app_release",
    "Downloading app update": "status.downloading_app_update",
    "Ready to restart": "status.ready_to_restart",
}

PRESET_KEYS = [
    "best-video",
    "video-1080p",
    "video-720p",
    "video-480p",
    "audio-mp3",
    "audio-m4a",
]


class YtDlpHelperApp:
    def __init__(self, root: tk.Tk) -> None:
        self.root = root
        self.root.title("YouTube Download Helper")
        self.root.geometry("760x430")
        self.root.minsize(700, 400)

        self.paths = get_app_paths()
        self.settings = load_settings(self.paths)
        self.language = normalize_language(self.settings.language)
        self.paths = replace(self.paths, download_dir=Path(self.settings.download_dir).expanduser())
        ensure_app_dirs(self.paths)
        self.downloader = DownloadService(self.paths)
        self.update_service = UpdateService(self.paths)
        self.activity_log = ActivityLogStore(self.paths)
        self.worker_thread: threading.Thread | None = None
        self.message_queue: queue.Queue[tuple[str, object]] = queue.Queue()
        self.ytdlp_version_cache: str | None = None
        self.ytdlp_version_cache_ready = False
        self.log_window: tk.Toplevel | None = None
        self.log_text: tk.Text | None = None
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
        self.cookie_status_var = tk.StringVar(value=self._localized_cookie_status())
        self.status_key = "status.ready"
        self.status_var = tk.StringVar(value=self._t(self.status_key))
        self.speed_var = tk.StringVar(value=self._t("status.speed_empty"))
        self.download_folder_var = tk.StringVar(value=str(self.paths.download_dir))
        self.progress_var = tk.IntVar(value=0)

        self._build_ui()
        self.url_var.trace_add("write", self._on_url_changed)
        self.root.after(150, self._poll_worker_messages)

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

        self._form_label(form, "field.cookies").grid(row=3, column=0, sticky="w", padx=(0, 12), pady=(0, 12))
        cookies_row = ttk.Frame(form)
        cookies_row.grid(row=3, column=1, sticky="ew", pady=(0, 12))
        cookies_row.columnconfigure(0, weight=1)
        ttk.Label(cookies_row, textvariable=self.cookie_status_var).grid(row=0, column=0, sticky="w")
        paste_cookies_button = ttk.Button(cookies_row, text=self._t("button.paste_cookies"), command=self._paste_cookies)
        self.button_widgets["button.paste_cookies"] = paste_cookies_button
        paste_cookies_button.grid(
            row=0, column=1, sticky="e", padx=(10, 0)
        )

        self._form_label(form, "field.downloads_folder").grid(
            row=4, column=0, sticky="w", padx=(0, 12), pady=(0, 12)
        )
        download_folder_row = ttk.Frame(form)
        download_folder_row.grid(row=4, column=1, sticky="ew", pady=(0, 12))
        download_folder_row.columnconfigure(0, weight=1)
        ttk.Entry(download_folder_row, textvariable=self.download_folder_var).grid(
            row=0, column=0, sticky="ew"
        )
        browse_button = ttk.Button(download_folder_row, text=self._t("button.browse"), command=self._choose_download_folder)
        self.button_widgets["button.browse"] = browse_button
        browse_button.grid(
            row=0, column=1, sticky="e", padx=(10, 0)
        )
        self.open_downloads_icon = self._create_open_folder_icon()
        ttk.Button(
            download_folder_row,
            image=self.open_downloads_icon,
            command=self._open_downloads,
            width=3,
        ).grid(
            row=0, column=2, sticky="e", padx=(10, 0)
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

        progress_frame = ttk.Frame(container)
        progress_frame.grid(row=4, column=0, sticky="ew", pady=(0, 12))
        progress_frame.columnconfigure(0, weight=1)

        ttk.Label(progress_frame, textvariable=self.status_var).grid(row=0, column=0, sticky="w")
        ttk.Label(progress_frame, textvariable=self.speed_var).grid(
            row=0, column=1, sticky="e", padx=(12, 0)
        )
        self.progress = ttk.Progressbar(progress_frame, maximum=100, variable=self.progress_var)
        self.progress.grid(row=1, column=0, columnspan=2, sticky="ew", pady=(8, 0))

    def _form_label(self, parent: ttk.Frame, key: str) -> ttk.Label:
        label = ttk.Label(parent, text=self._t(key))
        self.label_widgets[key] = label
        return label

    def _build_menu(self) -> None:
        menu_bar = tk.Menu(self.root)
        file_menu = tk.Menu(menu_bar, tearoff=False)
        file_menu.add_command(label=self._t("menu.settings"), command=self._show_settings)
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

    def _show_settings(self) -> None:
        dialog = tk.Toplevel(self.root)
        dialog.title(self._t("settings.title"))
        dialog.resizable(False, False)
        dialog.transient(self.root)
        dialog.grab_set()

        selected_language = tk.StringVar()
        language_pairs = language_options(self.language)
        language_labels = [label for label, _ in language_pairs]
        selected_language.set(self._language_label_for_code(language_pairs, self.language))

        frame = ttk.Frame(dialog, padding=16)
        frame.grid(row=0, column=0, sticky="nsew")
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

        button_bar = ttk.Frame(frame)
        button_bar.grid(row=1, column=0, columnspan=2, sticky="e")
        ttk.Button(button_bar, text=self._t("button.cancel"), command=dialog.destroy).pack(
            side="right", padx=(8, 0)
        )
        ttk.Button(
            button_bar,
            text=self._t("button.save"),
            command=lambda: self._save_settings_dialog(dialog, selected_language.get(), language_pairs),
        ).pack(side="right")

        language_combo.focus_set()
        dialog.wait_window()

    def _save_settings_dialog(
        self,
        dialog: tk.Toplevel,
        selected_label: str,
        language_pairs: list[tuple[str, str]],
    ) -> None:
        selected_language = self._language_code_for_label(language_pairs, selected_label)
        if selected_language:
            self.language = selected_language
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

        self.log_window = log_window
        self.log_text = text
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

    def _reload_activity_log_window(self) -> None:
        if not self.log_text:
            return

        lines = self.activity_log.read_all_lines()
        self.log_text.configure(state="normal")
        self.log_text.delete("1.0", "end")
        if lines:
            self.log_text.insert("end", "\n".join(lines) + "\n")
        self.log_text.see("end")
        self.log_text.configure(state="disabled")

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
        if self.worker_thread and self.worker_thread.is_alive():
            messagebox.showinfo(self._t("dialog.task_in_progress.title"), self._t("dialog.task_in_progress.message"))
            return

        request = DownloadRequest(
            url=self.url_var.get().strip(),
            preset=self.preset_var.get(),
            playlist=playlist,
        )

        if not self._apply_download_folder():
            return

        self._persist_settings()
        self._set_action_buttons_state("disabled")
        self.progress_var.set(0)
        self.speed_var.set(self._t("status.speed_empty"))
        self._append_log("")
        self._append_log(f"Starting download for {request.url}")

        self.worker_thread = threading.Thread(target=self._run_download, args=(request,), daemon=True)
        self.worker_thread.start()

    def _run_download(self, request: DownloadRequest) -> None:
        try:
            self.downloader.download(request, self._queue_status, self._queue_log)
        except Exception as exc:  # noqa: BLE001
            self.message_queue.put(("error", str(exc)))
        else:
            self.message_queue.put(("done", "status.download_completed"))

    def _start_update(self) -> None:
        if self.worker_thread and self.worker_thread.is_alive():
            messagebox.showinfo(self._t("dialog.task_in_progress.title"), self._t("dialog.task_in_progress.message"))
            return

        self._set_action_buttons_state("disabled")
        self.progress_var.set(0)
        self.speed_var.set(self._t("status.speed_empty"))
        self._append_log("")
        self._append_log("Starting update")
        self._set_status_key("queued", "status.updating_runtime_tools")

        self.worker_thread = threading.Thread(target=self._run_update, daemon=True)
        self.worker_thread.start()

    def _run_update(self) -> None:
        try:
            result = self.update_service.update(self._queue_log, self._queue_status)
        except Exception as exc:  # noqa: BLE001
            self.message_queue.put(("update_error", str(exc)))
        else:
            self.message_queue.put(("update_done", result))

    def _queue_status(self, status: str, message: str) -> None:
        self.message_queue.put(("status", f"{status}|{message}"))

    def _queue_log(self, message: str) -> None:
        self.message_queue.put(("log", message))

    def _poll_worker_messages(self) -> None:
        while True:
            try:
                kind, payload = self.message_queue.get_nowait()
            except queue.Empty:
                break

            if kind == "status":
                status, message = payload.split("|", 1)
                self._set_status(status, self._localized_worker_status(message))
            elif kind == "log":
                self._append_log(payload)
            elif kind == "error":
                self._set_status("failed", payload)
                self._set_action_buttons_state("normal")
                messagebox.showerror(self._t("dialog.download_failed.title"), payload)
            elif kind == "done":
                self._set_status_key("completed", str(payload))
                self._set_action_buttons_state("normal")
                messagebox.showinfo(
                    self._t("dialog.download_finished.title"),
                    self._t(str(payload)),
                )
            elif kind == "update_error":
                self._set_status("failed", str(payload))
                self._set_action_buttons_state("normal")
                messagebox.showerror(self._t("dialog.update_failed.title"), payload)
            elif kind == "update_done":
                assert isinstance(payload, AppUpdateResult)
                self._refresh_ytdlp_version_cache()
                self._set_status("completed", payload.message)
                self._set_action_buttons_state("normal")
                if payload.restart_ready and payload.restart_script:
                    confirmed = messagebox.askyesno(
                        self._t("dialog.restart_to_update.title"),
                        self._t("dialog.restart_to_update.message"),
                        parent=self.root,
                    )
                    if confirmed:
                        self._set_status_key("completed", "status.ready_to_restart")
                        start_restart_script(payload.restart_script)
                        self.root.destroy()
                    else:
                        messagebox.showinfo(
                            self._t("dialog.update_ready.title"),
                            self._t("dialog.update_ready.message"),
                            parent=self.root,
                        )
                else:
                    messagebox.showinfo(self._t("dialog.update_finished.title"), payload.message, parent=self.root)

        if self.worker_thread and not self.worker_thread.is_alive():
            self._set_action_buttons_state("normal")

        self.root.after(150, self._poll_worker_messages)

    def _set_status(self, status: str, message: str) -> None:
        if status == "speed":
            self.speed_var.set(self._t("status.speed", speed=message) if message else self._t("status.speed_empty"))
            return

        self.status_key = None
        self.status_var.set(message)
        if status == "downloading":
            percent = _percent_from_message(message)
            self.progress_var.set(percent if percent is not None else 10)
        elif status == "installing":
            percent = _percent_from_message(message)
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
        self.settings = Settings(
            preset=self.preset_var.get(),
            download_dir=str(self.paths.download_dir),
            language=self.language,
        )
        save_settings(self.paths, self.settings)

    def _choose_download_folder(self) -> None:
        current_dir = self._download_folder_from_entry()
        initial_dir = current_dir if current_dir and current_dir.exists() else Path.home()
        selected_dir = filedialog.askdirectory(
            parent=self.root,
            title=self._t("dialog.choose_downloads.title"),
            initialdir=str(initial_dir),
        )
        if selected_dir:
            self.download_folder_var.set(selected_dir)
            if self._apply_download_folder():
                self._persist_settings()

    def _apply_download_folder(self) -> bool:
        download_dir = self._download_folder_from_entry()
        if not download_dir:
            message = self._t("dialog.downloads_required.message")
            self._set_status("failed", message)
            self._append_log(message)
            messagebox.showerror(self._t("dialog.downloads_required.title"), message)
            return False

        try:
            download_dir.mkdir(parents=True, exist_ok=True)
        except OSError as exc:
            message = self._t("dialog.downloads_unavailable.message", error=exc)
            self._set_status("failed", message)
            self._append_log(message)
            messagebox.showerror(self._t("dialog.downloads_unavailable.title"), message)
            return False

        self.paths = replace(self.paths, download_dir=download_dir)
        self.downloader = DownloadService(self.paths)
        self.update_service = UpdateService(self.paths)
        self.download_folder_var.set(str(download_dir))
        return True

    def _download_folder_from_entry(self) -> Path | None:
        raw_path = self.download_folder_var.get().strip()
        if not raw_path:
            return None
        return Path(raw_path).expanduser()

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
        self.file_menu.entryconfigure(1, label=self._t("menu.activity_log"))
        self.help_menu.entryconfigure(0, label=self._t("menu.update"))
        self.help_menu.entryconfigure(1, label=self._t("menu.about"))

        self.preset_combo.configure(values=self._preset_labels())
        self.preset_label_var.set(self._preset_label(self.preset_var.get()))
        self.archive_status_var.set(self._t(self.archive_status_key))
        self.cookie_status_var.set(self._localized_cookie_status())
        if getattr(self, "status_key", None):
            self.status_var.set(self._t(self.status_key, **getattr(self, "status_params", {})))
        if self.log_window and self.log_window.winfo_exists():
            self.log_window.title(self._t("menu.activity_log"))

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

    def _localized_cookie_status(self) -> str:
        status = get_cookie_status(self.paths)
        if status == "No cookies saved":
            return self._t("cookies.none")
        if status.startswith("Saved "):
            return self._t("cookies.saved", timestamp=status.removeprefix("Saved "))
        return status

    def _localized_worker_status(self, message: str) -> str:
        key = STATUS_MESSAGE_KEYS.get(message)
        if key:
            return self._t(key)

        download_match = re.fullmatch(r"Downloading (\d+)%", message)
        if download_match:
            return self._t("status.downloading_percent", percent=download_match.group(1))

        tool_percent_match = re.fullmatch(r"Downloading (.+) (\d+)%", message)
        if tool_percent_match:
            return self._t(
                "status.downloading_tool_percent",
                tool_name=tool_percent_match.group(1),
                percent=tool_percent_match.group(2),
            )

        tool_mb_match = re.fullmatch(r"Downloading (.+) ([0-9.]+) MB", message)
        if tool_mb_match:
            return self._t(
                "status.downloading_tool_mb",
                tool_name=tool_mb_match.group(1),
                size=tool_mb_match.group(2),
            )

        return message

    def _t(self, key: str, **params: object) -> str:
        return translate(getattr(self, "language", "tr"), key, **params)


def main() -> None:
    root = tk.Tk()
    app = YtDlpHelperApp(root)
    root.mainloop()


def _percent_from_message(message: str) -> int | None:
    percent_index = message.find("%")
    if percent_index == -1:
        return None
    digits = []
    for character in reversed(message[:percent_index]):
        if not character.isdigit():
            if digits:
                break
            continue
        digits.append(character)
    if not digits:
        return None
    return max(0, min(100, int("".join(reversed(digits)))))
