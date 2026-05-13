from __future__ import annotations

from dataclasses import asdict, dataclass, replace
from datetime import datetime, timezone
import json
from pathlib import Path
import queue
import threading
from typing import Callable, Literal
from uuid import uuid4

from .config import AppPaths
from .downloader import DownloadRequest, DownloadService
from .worker_status import percent_from_message


QUEUE_SCHEMA_VERSION = 1
QUEUE_FILE = "queue.json"
QUEUE_STATUSES = ("queued", "running", "completed", "failed", "skipped")
QueueStatus = Literal["queued", "running", "completed", "failed", "skipped"]


@dataclass(frozen=True)
class QueueItem:
    id: str
    url: str
    preset: str
    playlist: bool
    download_dir: str
    filename_template: str
    added_at: str
    status: QueueStatus = "queued"
    name: str = ""
    progress: int | None = None
    speed: str = ""
    error: str = ""


@dataclass(frozen=True)
class QueueEvent:
    kind: str
    item_id: str = ""
    message: str = ""


class QueueStore:
    def __init__(self, path: Path) -> None:
        self.path = path
        self._items: list[QueueItem] = []
        self._lock = threading.RLock()

    @classmethod
    def for_paths(cls, paths: AppPaths) -> "QueueStore":
        return cls(paths.data_dir / QUEUE_FILE)

    def load(self) -> list[QueueItem]:
        with self._lock:
            self._items = self._read_items()
            self.save()
            return list(self._items)

    def items(self) -> list[QueueItem]:
        with self._lock:
            return list(self._items)

    def add(
        self,
        url: str,
        preset: str,
        playlist: bool,
        download_dir: str,
        filename_template: str,
    ) -> QueueItem:
        item = QueueItem(
            id=str(uuid4()),
            url=url.strip(),
            preset=preset,
            playlist=playlist,
            download_dir=download_dir,
            filename_template=filename_template,
            added_at=datetime.now(timezone.utc).isoformat(timespec="seconds"),
            name=_fallback_name(url),
        )
        with self._lock:
            self._items.append(item)
            self.save()
        return item

    def replace(self, item: QueueItem) -> None:
        with self._lock:
            for index, existing in enumerate(self._items):
                if existing.id == item.id:
                    self._items[index] = item
                    self.save()
                    return

    def get(self, item_id: str) -> QueueItem | None:
        with self._lock:
            for item in self._items:
                if item.id == item_id:
                    return item
        return None

    def has_duplicate_url(self, url: str) -> bool:
        normalized = url.strip()
        return any(item.url == normalized for item in self.items())

    def remove(self, item_id: str) -> bool:
        with self._lock:
            item = self.get(item_id)
            if not item or item.status == "running":
                return False
            self._items = [existing for existing in self._items if existing.id != item_id]
            self.save()
            return True

    def clear_completed(self) -> None:
        with self._lock:
            self._items = [
                item for item in self._items if item.status not in {"completed", "skipped"}
            ]
            self.save()

    def retry(self, item_id: str) -> bool:
        with self._lock:
            item = self.get(item_id)
            if not item or item.status not in {"failed", "skipped"}:
                return False
            self.replace(replace(item, status="queued", progress=None, speed="", error=""))
            return True

    def move(self, item_id: str, direction: int) -> bool:
        with self._lock:
            index = next((i for i, item in enumerate(self._items) if item.id == item_id), None)
            if index is None:
                return False
            target = index + direction
            if target < 0 or target >= len(self._items):
                return False
            if self._items[index].status == "running" or self._items[target].status == "running":
                return False
            self._items[index], self._items[target] = self._items[target], self._items[index]
            self.save()
            return True

    def save(self) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        payload = {
            "version": QUEUE_SCHEMA_VERSION,
            "items": [asdict(item) for item in self._items],
        }
        self.path.write_text(json.dumps(payload, indent=2), encoding="utf-8")

    def _read_items(self) -> list[QueueItem]:
        if not self.path.exists():
            return []
        try:
            data = json.loads(self.path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            return []
        if data.get("version") != QUEUE_SCHEMA_VERSION:
            return []

        items = []
        for raw_item in data.get("items", []):
            item = _parse_item(raw_item)
            if item:
                if item.status == "running":
                    item = replace(item, status="failed", speed="", error="Interrupted while app was closed.")
                items.append(item)
        return items


class QueueRunner:
    def __init__(
        self,
        store: QueueStore,
        paths: AppPaths,
        log_callback: Callable[[str], None],
        downloader_factory: Callable[[AppPaths, str], DownloadService] | None = None,
    ) -> None:
        self._store = store
        self._paths = paths
        self._log_callback = log_callback
        self._downloader_factory = downloader_factory or DownloadService
        self._paused = True
        self._concurrency = 1
        self._running: set[str] = set()
        self._lock = threading.RLock()
        self._events: queue.Queue[QueueEvent] = queue.Queue()

    @property
    def is_running(self) -> bool:
        with self._lock:
            return bool(self._running)

    @property
    def is_paused(self) -> bool:
        with self._lock:
            return self._paused

    def resume(self, concurrency: int) -> None:
        with self._lock:
            self._paused = False
            self._concurrency = max(1, min(4, concurrency))
        self._schedule()

    def pause(self) -> None:
        with self._lock:
            self._paused = True
        self._events.put(QueueEvent("summary"))

    def poll_events(self) -> list[QueueEvent]:
        events = []
        while True:
            try:
                events.append(self._events.get_nowait())
            except queue.Empty:
                return events

    def notify_queue_changed(self) -> None:
        self._events.put(QueueEvent("summary"))
        self._schedule()

    def _schedule(self) -> None:
        with self._lock:
            if self._paused:
                self._events.put(QueueEvent("summary"))
                return
            slots = self._concurrency - len(self._running)
            if slots <= 0:
                self._events.put(QueueEvent("summary"))
                return
            candidates = [item for item in self._store.items() if item.status == "queued"]
            for item in candidates[:slots]:
                self._start_item(item)
            self._events.put(QueueEvent("summary"))

    def _start_item(self, item: QueueItem) -> None:
        running_item = replace(item, status="running", progress=None, speed="", error="")
        self._store.replace(running_item)
        self._running.add(item.id)
        self._events.put(QueueEvent("item", item.id))
        thread = threading.Thread(target=self._run_item, args=(running_item,), daemon=True)
        thread.start()

    def _run_item(self, item: QueueItem) -> None:
        final_status: QueueStatus = "completed"
        error = ""
        try:
            paths = replace(self._paths, download_dir=Path(item.download_dir).expanduser())
            service = self._downloader_factory(paths, item.filename_template)
            service.download(
                DownloadRequest(item.url, item.preset, item.playlist),
                lambda status, message: self._handle_status(item.id, status, message),
                lambda message: self._handle_log(item.id, message),
            )
            latest = self._store.get(item.id)
            if latest and latest.status == "skipped":
                final_status = "skipped"
        except Exception as exc:  # noqa: BLE001
            final_status = "failed"
            error = str(exc)
            self._log_callback(f"Queue item failed for {item.url}: {error}")

        with self._lock:
            latest = self._store.get(item.id)
            if latest:
                progress = 100 if final_status == "completed" else latest.progress
                self._store.replace(
                    replace(latest, status=final_status, progress=progress, speed="", error=error)
                )
            self._running.discard(item.id)
            self._events.put(QueueEvent("item", item.id))
        self._schedule()

    def _handle_status(self, item_id: str, status: str, message: str) -> None:
        item = self._store.get(item_id)
        if not item:
            return
        if status == "speed":
            updated = replace(item, speed=message)
        elif status == "name":
            updated = replace(item, name=message or item.name)
        elif status == "skipped":
            updated = replace(item, status="skipped", progress=None, speed="")
        else:
            updated = replace(item, progress=percent_from_message(message))
        self._store.replace(updated)
        self._events.put(QueueEvent("item", item_id))

    def _handle_log(self, item_id: str, message: str) -> None:
        self._log_callback(message)
        name = _name_from_output(message)
        if name:
            item = self._store.get(item_id)
            if item:
                self._store.replace(replace(item, name=name))
                self._events.put(QueueEvent("item", item_id))


def _parse_item(raw_item: object) -> QueueItem | None:
    if not isinstance(raw_item, dict):
        return None
    status = raw_item.get("status", "queued")
    if status not in QUEUE_STATUSES:
        return None
    try:
        item = QueueItem(
            id=str(raw_item["id"]),
            url=str(raw_item["url"]),
            preset=str(raw_item["preset"]),
            playlist=bool(raw_item.get("playlist", False)),
            download_dir=str(raw_item["download_dir"]),
            filename_template=str(raw_item["filename_template"]),
            added_at=str(raw_item["added_at"]),
            status=status,
            name=str(raw_item.get("name", "")),
            progress=_optional_int(raw_item.get("progress")),
            speed=str(raw_item.get("speed", "")),
            error=str(raw_item.get("error", "")),
        )
    except KeyError:
        return None
    if not item.url.strip() or not item.id.strip():
        return None
    return item


def _optional_int(value: object) -> int | None:
    if value is None:
        return None
    try:
        return max(0, min(100, int(value)))
    except (TypeError, ValueError):
        return None


def _fallback_name(url: str) -> str:
    return url.strip() or "Queued download"


def _name_from_output(line: str) -> str:
    markers = ("[download] Destination: ", "[ExtractAudio] Destination: ")
    for marker in markers:
        if marker in line:
            return Path(line.split(marker, 1)[1]).name
    return ""
