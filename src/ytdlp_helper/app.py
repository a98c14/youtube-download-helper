from __future__ import annotations

import queue
import subprocess
import threading
import tkinter as tk
from tkinter import messagebox, ttk

from .config import Settings, ensure_app_dirs, get_app_paths, load_settings, save_settings
from .cookies import get_cookie_status, save_cookie_text
from .downloader import DownloadRequest, DownloadService


PRESET_OPTIONS = [
    ("Best Video", "best-video"),
    ("Video 1080p", "video-1080p"),
    ("Video 720p", "video-720p"),
    ("Video 480p", "video-480p"),
    ("Audio MP3", "audio-mp3"),
    ("Audio M4A", "audio-m4a"),
]


class YtDlpHelperApp:
    def __init__(self, root: tk.Tk) -> None:
        self.root = root
        self.root.title("YouTube Download Helper")
        self.root.geometry("760x560")
        self.root.minsize(700, 520)

        self.paths = get_app_paths()
        ensure_app_dirs(self.paths)
        self.settings = load_settings(self.paths)
        self.downloader = DownloadService(self.paths)
        self.worker_thread: threading.Thread | None = None
        self.message_queue: queue.Queue[tuple[str, str]] = queue.Queue()

        self.url_var = tk.StringVar()
        self.preset_var = tk.StringVar(value=self.settings.preset)
        self.preset_label_var = tk.StringVar()
        self.cookie_status_var = tk.StringVar(value=get_cookie_status(self.paths))
        self.status_var = tk.StringVar(value="Ready")
        self.download_folder_var = tk.StringVar(value=str(self.paths.download_dir))
        self.progress_var = tk.IntVar(value=0)

        self._build_ui()
        self.root.after(150, self._poll_worker_messages)

    def _build_ui(self) -> None:
        style = ttk.Style()
        style.configure("Header.TLabel", font=("Segoe UI Semibold", 18))
        style.configure("Hint.TLabel", foreground="#4b5563")

        container = ttk.Frame(self.root, padding=20)
        container.pack(fill="both", expand=True)
        container.columnconfigure(0, weight=1)

        ttk.Label(container, text="YouTube Download Helper", style="Header.TLabel").grid(
            row=0, column=0, sticky="w"
        )
        ttk.Label(
            container,
            text="Paste a YouTube URL, choose a preset, and download with optional pasted cookies.",
            style="Hint.TLabel",
        ).grid(row=1, column=0, sticky="w", pady=(4, 16))

        form = ttk.Frame(container)
        form.grid(row=2, column=0, sticky="nsew")
        form.columnconfigure(1, weight=1)

        ttk.Label(form, text="URL").grid(row=0, column=0, sticky="w", padx=(0, 12), pady=(0, 12))
        ttk.Entry(form, textvariable=self.url_var).grid(row=0, column=1, sticky="ew", pady=(0, 12))

        ttk.Label(form, text="Preset").grid(row=1, column=0, sticky="w", padx=(0, 12), pady=(0, 12))
        preset_combo = ttk.Combobox(
            form,
            textvariable=self.preset_label_var,
            state="readonly",
            values=[label for label, _ in PRESET_OPTIONS],
        )
        preset_combo.grid(row=1, column=1, sticky="ew", pady=(0, 12))
        preset_combo.bind("<<ComboboxSelected>>", self._on_preset_changed)
        preset_combo.current(self._preset_index(self.settings.preset))
        self.preset_combo = preset_combo

        ttk.Label(form, text="Cookies").grid(row=2, column=0, sticky="w", padx=(0, 12), pady=(0, 12))
        cookies_row = ttk.Frame(form)
        cookies_row.grid(row=2, column=1, sticky="ew", pady=(0, 12))
        cookies_row.columnconfigure(0, weight=1)
        ttk.Label(cookies_row, textvariable=self.cookie_status_var).grid(row=0, column=0, sticky="w")
        ttk.Button(cookies_row, text="Paste Cookies", command=self._paste_cookies).grid(
            row=0, column=1, sticky="e", padx=(10, 0)
        )

        ttk.Label(form, text="Downloads Folder").grid(
            row=3, column=0, sticky="w", padx=(0, 12), pady=(0, 12)
        )
        ttk.Entry(form, textvariable=self.download_folder_var, state="readonly").grid(
            row=3, column=1, sticky="ew", pady=(0, 12)
        )

        button_bar = ttk.Frame(container)
        button_bar.grid(row=3, column=0, sticky="ew", pady=(16, 12))

        self.download_button = ttk.Button(button_bar, text="Download", command=self._start_download)
        self.download_button.pack(side="left")
        self.update_button = ttk.Button(button_bar, text="Update yt-dlp", command=self._start_update)
        self.update_button.pack(side="left", padx=(10, 0))
        ttk.Button(button_bar, text="Open Downloads Folder", command=self._open_downloads).pack(
            side="left", padx=(10, 0)
        )

        progress_frame = ttk.Frame(container)
        progress_frame.grid(row=4, column=0, sticky="ew", pady=(0, 12))
        progress_frame.columnconfigure(0, weight=1)

        ttk.Label(progress_frame, textvariable=self.status_var).grid(row=0, column=0, sticky="w")
        self.progress = ttk.Progressbar(progress_frame, maximum=100, variable=self.progress_var)
        self.progress.grid(row=1, column=0, sticky="ew", pady=(8, 0))

        ttk.Label(container, text="Activity Log", style="Hint.TLabel").grid(row=5, column=0, sticky="w")
        self.log_widget = tk.Text(container, height=16, wrap="word", state="disabled")
        self.log_widget.grid(row=6, column=0, sticky="nsew")

        container.rowconfigure(6, weight=1)

    def _preset_index(self, preset_key: str) -> int:
        for index, (_, value) in enumerate(PRESET_OPTIONS):
            if value == preset_key:
                return index
        return 0

    def _on_preset_changed(self, _event: object) -> None:
        selected_label = self.preset_combo.get()
        for label, value in PRESET_OPTIONS:
            if label == selected_label:
                self.preset_var.set(value)
                return

    def _start_download(self) -> None:
        if self.worker_thread and self.worker_thread.is_alive():
            messagebox.showinfo("Task in progress", "Please wait for the current task to finish.")
            return

        request = DownloadRequest(
            url=self.url_var.get().strip(),
            preset=self.preset_var.get(),
        )

        self._persist_settings()
        self._set_action_buttons_state("disabled")
        self.progress_var.set(0)
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
            self.message_queue.put(("done", "Download completed"))

    def _start_update(self) -> None:
        if self.worker_thread and self.worker_thread.is_alive():
            messagebox.showinfo("Task in progress", "Please wait for the current task to finish.")
            return

        self._set_action_buttons_state("disabled")
        self.progress_var.set(0)
        self._append_log("")
        self._append_log("Updating yt-dlp")
        self._set_status("queued", "Updating yt-dlp")

        self.worker_thread = threading.Thread(target=self._run_update, daemon=True)
        self.worker_thread.start()

    def _run_update(self) -> None:
        try:
            message = self.downloader.update_ytdlp(self._queue_log)
        except Exception as exc:  # noqa: BLE001
            self.message_queue.put(("update_error", str(exc)))
        else:
            self.message_queue.put(("update_done", message))

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
                self._set_status(status, message)
            elif kind == "log":
                self._append_log(payload)
            elif kind == "error":
                self._set_status("failed", payload)
                self._set_action_buttons_state("normal")
                messagebox.showerror("Download failed", payload)
            elif kind == "done":
                self._set_status("completed", payload)
                self._set_action_buttons_state("normal")
                messagebox.showinfo("Download finished", payload)
            elif kind == "update_error":
                self._set_status("failed", payload)
                self._set_action_buttons_state("normal")
                messagebox.showerror("Update failed", payload)
            elif kind == "update_done":
                self._set_status("completed", payload)
                self._set_action_buttons_state("normal")
                messagebox.showinfo("Update finished", payload)

        if self.worker_thread and not self.worker_thread.is_alive():
            self._set_action_buttons_state("normal")

        self.root.after(150, self._poll_worker_messages)

    def _set_status(self, status: str, message: str) -> None:
        self.status_var.set(message)
        if status == "downloading":
            digits = "".join(ch for ch in message if ch.isdigit())
            self.progress_var.set(int(digits) if digits else 10)
        elif status == "postprocessing":
            self.progress_var.set(95)
        elif status == "completed":
            self.progress_var.set(100)
        elif status in {"failed", "skipped"}:
            self.progress_var.set(0)
        elif status in {"queued", "resolving"}:
            self.progress_var.set(5)

    def _append_log(self, message: str) -> None:
        if not message:
            return
        self.log_widget.configure(state="normal")
        self.log_widget.insert("end", message.strip() + "\n")
        self.log_widget.see("end")
        self.log_widget.configure(state="disabled")

    def _persist_settings(self) -> None:
        self.settings = Settings(
            preset=self.preset_var.get(),
            download_dir=str(self.paths.download_dir),
        )
        save_settings(self.paths, self.settings)

    def _open_downloads(self) -> None:
        subprocess.Popen(["explorer.exe", str(self.paths.download_dir)])

    def _paste_cookies(self) -> None:
        try:
            text = self.root.clipboard_get()
        except tk.TclError:
            message = "Clipboard does not contain cookies.txt text."
            self._set_status("failed", message)
            self._append_log(message)
            return

        try:
            save_cookie_text(self.paths, text)
        except ValueError as exc:
            self._set_status("failed", str(exc))
            self._append_log(str(exc))
            return

        self.cookie_status_var.set(get_cookie_status(self.paths))
        self._set_status("queued", "Cookies saved")
        self._append_log(f"Saved cookies to {self.paths.cookies_file}")

    def _set_action_buttons_state(self, state: str) -> None:
        self.download_button.configure(state=state)
        self.update_button.configure(state=state)


def main() -> None:
    root = tk.Tk()
    app = YtDlpHelperApp(root)
    root.mainloop()
