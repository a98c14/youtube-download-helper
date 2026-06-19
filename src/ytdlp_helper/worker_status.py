from __future__ import annotations

from dataclasses import dataclass
from enum import Enum
from pathlib import Path
import queue
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


class RuntimeTool(Enum):
    YTDLP = "ytdlp"
    FFMPEG = "ffmpeg"
    DENO = "deno"

    @property
    def display_name(self) -> str:
        return _RUNTIME_TOOL_DISPLAY[self]


_RUNTIME_TOOL_DISPLAY = {
    RuntimeTool.YTDLP: "yt-dlp",
    RuntimeTool.FFMPEG: "ffmpeg",
    RuntimeTool.DENO: "Deno",
}


class RuntimeToolPhase(Enum):
    UPDATING = "updating"
    CHECKING = "checking"
    INSTALLING = "installing"
    DOWNLOADING = "downloading"


@dataclass(frozen=True)
class RuntimeToolStatus:
    tool: RuntimeTool
    phase: RuntimeToolPhase
    percent: int | None = None
    size_mb: str | None = None


class DownloadPhase(Enum):
    PREPARING = "preparing"
    RESOLVING_VIDEO = "resolving_video"
    RESOLVING_PLAYLIST = "resolving_playlist"
    DOWNLOADING = "downloading"
    FINALIZING = "finalizing"
    ARCHIVE_SKIPPED = "archive_skipped"


@dataclass(frozen=True)
class DownloadStatus:
    phase: DownloadPhase
    percent: int | None = None
    speed: str | None = None


class AppUpdatePhase(Enum):
    CHECKING = "checking"
    DOWNLOADING = "downloading"
    READY = "ready"


@dataclass(frozen=True)
class AppUpdateStatus:
    phase: AppUpdatePhase
    percent: int | None = None


StatusEvent = RuntimeToolStatus | DownloadStatus | AppUpdateStatus


class WorkerReporter(Protocol):
    def status(self, phase: WorkerPhase, event: StatusEvent) -> None: ...
    def log(self, message: str) -> None: ...

    @property
    def status_callback(self) -> Callable[[WorkerPhase, StatusEvent], None]: ...

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
    def show_status(self, phase: WorkerPhase, event: StatusEvent | str) -> None: ...
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
    event: StatusEvent


@dataclass(frozen=True)
class _SpeedEvent:
    speed: str


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

    def status(self, phase: WorkerPhase, event: StatusEvent) -> None:
        self._events.put(_StatusEvent(phase, event))

    def log(self, message: str) -> None:
        self._events.put(_LogEvent(message))

    @property
    def status_callback(self) -> Callable[[WorkerPhase, StatusEvent], None]:
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
            self._show_status(event.phase, event.event)
        elif isinstance(event, _SpeedEvent):
            self._ui.show_speed(event.speed)
        elif isinstance(event, _LogEvent):
            self._ui.append_log(event.message)
        elif isinstance(event, _ErrorEvent):
            self._show_error(event.message)
        elif isinstance(event, _DoneEvent):
            self._show_success(event.result)

    def _show_status(self, phase: WorkerPhase, event: StatusEvent) -> None:
        self._ui.show_status(phase, event)
        self._apply_phase_progress(phase, event_percent(event))

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


def event_percent(event: StatusEvent) -> int | None:
    if isinstance(event, RuntimeToolStatus):
        return event.percent
    if isinstance(event, DownloadStatus):
        return event.percent
    if isinstance(event, AppUpdateStatus):
        return event.percent
    return None


def status_event_to_key(event: StatusEvent) -> tuple[str, dict[str, object]]:
    if isinstance(event, RuntimeToolStatus):
        return _runtime_tool_status_key(event)
    if isinstance(event, DownloadStatus):
        return _download_status_key(event)
    if isinstance(event, AppUpdateStatus):
        return _app_update_status_key(event)
    raise TypeError(f"Unknown status event type: {type(event)}")


def _runtime_tool_status_key(event: RuntimeToolStatus) -> tuple[str, dict[str, object]]:
    tool_value = event.tool.value
    if event.phase == RuntimeToolPhase.UPDATING:
        return ("status.updating_runtime_tools", {})
    if event.phase == RuntimeToolPhase.CHECKING:
        return (f"status.checking_{tool_value}", {})
    if event.phase == RuntimeToolPhase.INSTALLING:
        return (f"status.installing_{tool_value}", {})
    if event.phase == RuntimeToolPhase.DOWNLOADING:
        if event.percent is not None:
            return ("status.downloading_tool_percent", {"tool_name": event.tool.display_name, "percent": event.percent})
        if event.size_mb is not None:
            return ("status.downloading_tool_mb", {"tool_name": event.tool.display_name, "size": event.size_mb})
        return ("status.downloading_tool_percent", {"tool_name": event.tool.display_name, "percent": 0})
    raise ValueError(f"Unknown RuntimeToolPhase: {event.phase}")


_DOWNLOAD_PHASE_KEYS = {
    DownloadPhase.PREPARING: "status.preparing_download",
    DownloadPhase.RESOLVING_VIDEO: "status.resolving_video",
    DownloadPhase.RESOLVING_PLAYLIST: "status.resolving_playlist",
    DownloadPhase.DOWNLOADING: "status.downloading_percent",
    DownloadPhase.FINALIZING: "status.finalizing_file",
    DownloadPhase.ARCHIVE_SKIPPED: "status.archive_skipped",
}


def _download_status_key(event: DownloadStatus) -> tuple[str, dict[str, object]]:
    key = _DOWNLOAD_PHASE_KEYS[event.phase]
    params: dict[str, object] = {}
    if event.phase == DownloadPhase.DOWNLOADING and event.percent is not None:
        params["percent"] = event.percent
    return (key, params)


_APP_UPDATE_PHASE_KEYS = {
    AppUpdatePhase.CHECKING: "status.checking_app_release",
    AppUpdatePhase.DOWNLOADING: "status.downloading_app_update",
    AppUpdatePhase.READY: "status.ready_to_restart",
}


def _app_update_status_key(event: AppUpdateStatus) -> tuple[str, dict[str, object]]:
    return (_APP_UPDATE_PHASE_KEYS[event.phase], {})
