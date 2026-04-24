from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import queue
import re
import threading
from typing import Callable, Generic, Literal, Protocol, TypeVar


WorkerPhase = Literal[
    "queued",
    "resolving",
    "downloading",
    "installing",
    "postprocessing",
    "completed",
    "failed",
    "skipped",
    "speed",
]
WorkerKind = Literal["download", "update"]
T = TypeVar("T")


class WorkerReporter(Protocol):
    def status(self, phase: WorkerPhase, message: str) -> None: ...
    def log(self, message: str) -> None: ...

    @property
    def status_callback(self) -> Callable[[str, str], None]: ...

    @property
    def log_callback(self) -> Callable[[str], None]: ...


@dataclass(frozen=True)
class WorkerTask(Generic[T]):
    kind: WorkerKind
    initial_status_key: str | None
    initial_log: str
    run: Callable[[WorkerReporter], T]
    success: Callable[[T, "WorkerUi"], None]
    error_title_key: str


class WorkerUi(Protocol):
    def set_busy(self, busy: bool) -> None: ...
    def show_status(self, phase: WorkerPhase, message: str) -> None: ...
    def show_status_key(self, phase: WorkerPhase, key: str, **params: object) -> None: ...
    def show_speed(self, speed: str | None) -> None: ...
    def show_progress(self, value: int) -> None: ...
    def append_log(self, message: str) -> None: ...
    def info(self, title_key: str, message: str, *, localized: bool = False) -> None: ...
    def error(self, title_key: str, message: str) -> None: ...
    def confirm(self, title_key: str, message_key: str) -> bool: ...
    def restart(self, script_path: Path) -> None: ...
    def refresh_runtime_version_cache(self) -> None: ...


@dataclass(frozen=True)
class _StatusEvent:
    phase: WorkerPhase
    message: str


@dataclass(frozen=True)
class _LogEvent:
    message: str


@dataclass(frozen=True)
class _ErrorEvent:
    message: str


@dataclass(frozen=True)
class _DoneEvent(Generic[T]):
    result: T


class _QueueWorkerReporter:
    def __init__(self, events: "queue.Queue[object]") -> None:
        self._events = events

    def status(self, phase: WorkerPhase, message: str) -> None:
        self._events.put(_StatusEvent(phase, message))

    def log(self, message: str) -> None:
        self._events.put(_LogEvent(message))

    @property
    def status_callback(self) -> Callable[[str, str], None]:
        return self.status

    @property
    def log_callback(self) -> Callable[[str], None]:
        return self.log


class WorkerStatusPipeline:
    def __init__(
        self,
        ui: WorkerUi,
        translate: Callable[..., str],
        after: Callable[[int, Callable[[], None]], None],
        poll_ms: int = 150,
    ) -> None:
        self._ui = ui
        self._translate = translate
        self._after = after
        self._poll_ms = poll_ms
        self._events: queue.Queue[object] = queue.Queue()
        self._thread: threading.Thread | None = None
        self._task: WorkerTask[object] | None = None

    def start(self, task: WorkerTask[T]) -> bool:
        if self.is_busy:
            return False

        self._task = task  # type: ignore[assignment]
        self._ui.set_busy(True)
        self._ui.show_progress(0)
        self._ui.show_speed(None)
        self._ui.append_log("")
        self._ui.append_log(task.initial_log)
        if task.initial_status_key:
            self._ui.show_status_key("queued", task.initial_status_key)
            self._apply_phase_progress("queued", None)

        reporter = _QueueWorkerReporter(self._events)
        self._thread = threading.Thread(target=self._run_task, args=(task, reporter), daemon=True)
        self._thread.start()
        return True

    @property
    def is_busy(self) -> bool:
        return bool(self._thread and self._thread.is_alive())

    def poll(self) -> None:
        while True:
            try:
                event = self._events.get_nowait()
            except queue.Empty:
                break
            self._handle_event(event)

        if self._thread and not self._thread.is_alive():
            self._ui.set_busy(False)
            self._thread = None
            self._task = None

        self._after(self._poll_ms, self.poll)

    def _run_task(self, task: WorkerTask[T], reporter: WorkerReporter) -> None:
        try:
            result = task.run(reporter)
        except Exception as exc:  # noqa: BLE001
            self._events.put(_ErrorEvent(str(exc)))
        else:
            self._events.put(_DoneEvent(result))

    def _handle_event(self, event: object) -> None:
        if isinstance(event, _StatusEvent):
            self._show_status(event.phase, event.message)
        elif isinstance(event, _LogEvent):
            self._ui.append_log(event.message)
        elif isinstance(event, _ErrorEvent):
            self._show_error(event.message)
        elif isinstance(event, _DoneEvent):
            self._show_success(event.result)

    def _show_status(self, phase: WorkerPhase, message: str) -> None:
        if phase == "speed":
            self._ui.show_speed(message or None)
            return

        normalized = normalize_worker_status(message, self._translate)
        self._ui.show_status(phase, normalized.message)
        self._apply_phase_progress(phase, normalized.percent)

    def _show_error(self, message: str) -> None:
        self._ui.show_status("failed", message)
        self._apply_phase_progress("failed", None)
        if self._task:
            self._ui.error(self._task.error_title_key, message)
        self._ui.set_busy(False)

    def _show_success(self, result: object) -> None:
        if self._task:
            self._task.success(result, self._ui)
        self._ui.set_busy(False)

    def _apply_phase_progress(self, phase: WorkerPhase, percent: int | None) -> None:
        if phase == "downloading":
            self._ui.show_progress(percent if percent is not None else 10)
            return
        if phase == "installing":
            self._ui.show_progress(percent if percent is not None else 5)
            self._ui.show_speed(None)
            return
        if phase == "postprocessing":
            self._ui.show_progress(95)
            self._ui.show_speed(None)
            return
        if phase == "completed":
            self._ui.show_progress(100)
            self._ui.show_speed(None)
            return
        if phase in {"failed", "skipped"}:
            self._ui.show_progress(0)
            self._ui.show_speed(None)
            return
        if phase in {"queued", "resolving"}:
            self._ui.show_progress(5)
            self._ui.show_speed(None)


@dataclass(frozen=True)
class NormalizedWorkerStatus:
    message: str
    percent: int | None = None


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


def normalize_worker_status(
    message: str,
    translate: Callable[..., str],
) -> NormalizedWorkerStatus:
    key = STATUS_MESSAGE_KEYS.get(message)
    if key:
        return NormalizedWorkerStatus(translate(key), percent_from_message(message))

    download_match = re.fullmatch(r"Downloading (\d+)%", message)
    if download_match:
        percent = int(download_match.group(1))
        return NormalizedWorkerStatus(translate("status.downloading_percent", percent=percent), percent)

    tool_percent_match = re.fullmatch(r"Downloading (.+) (\d+)%", message)
    if tool_percent_match:
        percent = int(tool_percent_match.group(2))
        return NormalizedWorkerStatus(
            translate(
                "status.downloading_tool_percent",
                tool_name=tool_percent_match.group(1),
                percent=percent,
            ),
            percent,
        )

    tool_mb_match = re.fullmatch(r"Downloading (.+) ([0-9.]+) MB", message)
    if tool_mb_match:
        return NormalizedWorkerStatus(
            translate(
                "status.downloading_tool_mb",
                tool_name=tool_mb_match.group(1),
                size=tool_mb_match.group(2),
            )
        )

    return NormalizedWorkerStatus(message, percent_from_message(message))


def percent_from_message(message: str) -> int | None:
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
