from __future__ import annotations

from dataclasses import asdict, dataclass, fields, replace
from datetime import datetime, timezone
import json
from pathlib import Path
import queue
import re
import threading
from typing import Callable, Literal
from uuid import uuid4

from .config import AppPaths
from .database import Database, DATABASE_FILE
from .dependencies import RuntimeToolResolver
from .downloader import DownloadCompletion, DownloadRequest, DownloadService
from .worker_status import StatusEvent, event_percent


QUEUE_FILE = "queue.json"
QUEUE_STATUSES = ("queued", "running", "completed", "failed")
QueueStatus = Literal["queued", "running", "completed", "failed"]


@dataclass(frozen=True)
class QueueItem:
    id: str
    url: str
    preset: str
    download_dir: str
    filename_template: str
    organize_by_channel: bool = True
    added_at: str = ""
    category_id: str = "default"
    category_name: str = "Default"
    status: QueueStatus = "queued"
    name: str = ""
    progress: int | None = None
    speed: str = ""
    error: str = ""
    output_path: str = ""
    previous_output_path: str = ""
    warning: str = ""
    source_type: str = "manual"
    playlist_id: str = ""
    playlist_position: int | None = None
    playlist_title: str = ""
    extractor: str = ""
    media_id: str = ""
    completed_at: str = ""


@dataclass(frozen=True)
class QueueEvent:
    kind: str
    item_id: str = ""
    message: str = ""


class QueueStore:
    def __init__(self, path: Path, database_path: Path | None = None) -> None:
        self.path = path
        self.database = Database(database_path or path.parent / DATABASE_FILE)
        self._items: list[QueueItem] = []
        self._lock = threading.RLock()
        self._database_ready = False
        self._legacy_loaded = False

    def _ensure_database(self) -> None:
        if not self._database_ready:
            self.database.initialize()
            self._database_ready = True

    @classmethod
    def for_paths(cls, paths: AppPaths) -> "QueueStore":
        return cls(paths.data_dir / QUEUE_FILE, paths.data_dir / DATABASE_FILE)

    def load(self) -> list[QueueItem]:
        with self._lock:
            self._ensure_database()
            if self._legacy_loaded:
                self._items = self._read_items()
                self._write_all()
                return list(self._items)
            self._import_legacy_once()
            self._items = self._read_database_items()
            for item in list(self._items):
                if item.status == "running":
                    self.replace(replace(item, status="failed", speed="", error="Interrupted while app was closed."))
            return list(self._items)

    def items(self) -> list[QueueItem]:
        with self._lock:
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
        *,
        source_type: str = "manual",
        playlist_id: str = "",
        playlist_position: int | None = None,
        playlist_title: str = "",
        media_id: str = "",
        extractor: str = "",
    ) -> QueueItem:
        item = QueueItem(
            id=str(uuid4()),
            url=url.strip(),
            preset=preset,
            download_dir=download_dir,
            filename_template=filename_template,
            organize_by_channel=organize_by_channel,
            added_at=datetime.now(timezone.utc).isoformat(timespec="seconds"),
            category_id=category_id,
            category_name=category_name,
            name=_fallback_name(url),
            source_type=source_type,
            playlist_id=playlist_id,
            playlist_position=playlist_position,
            playlist_title=playlist_title,
            media_id=media_id,
            extractor=extractor,
        )
        with self._lock:
            self._items.append(item)
            self._write_all()
        return item

    def add_many(self, items: list[dict[str, object]]) -> list[QueueItem]:
        created = [QueueItem(
            id=str(uuid4()), url=str(raw["url"]).strip(), preset=str(raw["preset"]),
            download_dir=str(raw["download_dir"]),
            filename_template=str(raw["filename_template"]),
            organize_by_channel=bool(raw.get("organize_by_channel", True)),
            added_at=datetime.now(timezone.utc).isoformat(timespec="seconds"),
            category_id=str(raw.get("category_id", "default")),
            category_name=str(raw.get("category_name", "Default")),
            name=_fallback_name(str(raw["url"])),
            source_type=str(raw.get("source_type", "tracker")),
            playlist_id=str(raw.get("playlist_id", "")),
            playlist_position=int(raw["playlist_position"]) if raw.get("playlist_position") is not None else None,
            playlist_title=str(raw.get("playlist_title", "")),
            media_id=str(raw.get("media_id", "")),
            extractor=str(raw.get("extractor", "")),
        ) for raw in items]
        with self._lock:
            previous = list(self._items)
            self._items.extend(created)
            try:
                self._ensure_database()
                with self.database.connect() as connection:
                    connection.execute("BEGIN IMMEDIATE")
                    self._write_all_to_connection(connection)
                    connection.commit()
            except Exception:
                self._items = previous
                raise
        return created

    def replace(self, item: QueueItem) -> None:
        with self._lock:
            for index, existing in enumerate(self._items):
                if existing.id == item.id:
                    self._items[index] = item
                    self._write_all()
                    return

    def get(self, item_id: str) -> QueueItem | None:
        with self._lock:
            for item in self._items:
                if item.id == item_id:
                    return item
        return None

    def find_existing(self, media_id: str, preset: str, download_dir: str,
                      filename_template: str, organize_by_channel: bool,
                      playlist_id: str) -> QueueItem | None:
        normalized = media_id.strip()
        if not normalized:
            return None
        with self._lock:
            for item in self._items:
                if (item.media_id.strip() == normalized
                        and item.preset == preset
                        and item.download_dir == download_dir
                        and item.filename_template == filename_template
                        and item.organize_by_channel == organize_by_channel
                        and item.playlist_id == playlist_id):
                    return item
        return None

    def remove(self, item_id: str) -> bool:
        with self._lock:
            item = self.get(item_id)
            if not item or item.status == "running":
                return False
            self._items = [existing for existing in self._items if existing.id != item_id]
            self._write_all()
            return True

    def clear_completed(self) -> None:
        with self._lock:
            self._items = [item for item in self._items if item.status not in {"completed"}]
            self._write_all()

    def retry(self, item_id: str) -> bool:
        with self._lock:
            item = self.get(item_id)
            if not item or item.status not in {"failed", "completed"}:
                return False
            self.replace(replace(
                item, status="queued", progress=None, speed="", error="",
                previous_output_path=item.output_path, output_path="",
            ))
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
            self._write_all()
            return True

    def save(self) -> None:
        with self._lock:
            self._write_all()

    def _write_all(self) -> None:
        self._ensure_database()
        with self.database.connect() as connection:
            connection.execute("BEGIN IMMEDIATE")
            self._write_all_to_connection(connection)
            connection.commit()

    def _write_all_to_connection(self, connection: object) -> None:
        connection.execute("DELETE FROM queue_items")
        for position, item in enumerate(self._items):
            connection.execute(
                "INSERT INTO queue_items(id,position,url,preset,download_dir,filename_template,"
                "organize_by_channel,added_at,category_id,category_name,status,name,progress,speed,"
                "error,output_path,previous_output_path,warning,source_type,playlist_id,"
                "playlist_position,playlist_title,extractor,media_id,completed_at) "
                "VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                (item.id, position, item.url, item.preset, item.download_dir, item.filename_template,
                 int(item.organize_by_channel), item.added_at, item.category_id, item.category_name,
                 item.status, item.name, item.progress, item.speed, item.error, item.output_path,
                 item.previous_output_path, item.warning, item.source_type,
                 item.playlist_id or None,                  item.playlist_position, item.playlist_title or None,
                 item.extractor or "", item.media_id or "", item.completed_at or ""),
            )

    def _read_database_items(self) -> list[QueueItem]:
        self._ensure_database()
        names = {field.name for field in fields(QueueItem)}
        with self.database.connect() as connection:
            rows = connection.execute("SELECT * FROM queue_items ORDER BY position").fetchall()
        result = []
        for row in rows:
            row_dict = dict(row)
            row_dict["organize_by_channel"] = bool(row_dict.get("organize_by_channel", 1))
            kwargs = {name: row_dict[name] for name in names if name in row_dict}
            result.append(QueueItem(**kwargs))
        return result

    def _import_legacy_once(self) -> None:
        self._ensure_database()
        with self.database.connect() as connection:
            if connection.execute("SELECT COUNT(*) FROM queue_items").fetchone()[0] or not self.path.exists():
                return
        imported = self._read_items()
        if not imported:
            return
        self._legacy_loaded = True
        self._items = imported
        self._write_all()
        backup = self.path.with_suffix(self.path.suffix + ".migrated")
        if not backup.exists():
            try:
                backup.write_bytes(self.path.read_bytes())
            except OSError:
                pass
        self.path.write_text(json.dumps({"items": [asdict(i) for i in imported]}, indent=2), encoding="utf-8")

    def _read_items(self) -> list[QueueItem]:
        if not self.path.exists():
            return []
        try:
            data = json.loads(self.path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
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
        downloader_factory: Callable[[AppPaths, str, bool], DownloadService] | None = None,
        organize_by_channel_provider: Callable[[], bool] | None = None,
        runtime_tools: RuntimeToolResolver | None = None,
    ) -> None:
        self._store = store
        self._paths = paths
        self._log_callback = log_callback
        self._uses_default_downloader_factory = downloader_factory is None
        self._downloader_factory = downloader_factory or DownloadService
        self._runtime_tools = runtime_tools or RuntimeToolResolver(paths)
        self._organize_by_channel_provider = organize_by_channel_provider or (lambda: True)
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
        completed_download: DownloadCompletion | None = None
        try:
            paths = replace(self._paths, download_dir=Path(item.download_dir).expanduser())
            if self._uses_default_downloader_factory:
                service = DownloadService(
                    paths,
                    item.filename_template,
                    item.organize_by_channel,
                    self._runtime_tools,
                )
            else:
                service = self._downloader_factory(
                    paths,
                    item.filename_template,
                    self._organize_by_channel_provider(),
                )
            callbacks = (
                DownloadRequest(item.url, item.preset),
                lambda status, message: self._handle_status(item.id, status, message),
                lambda message: self._handle_log(item.id, message),
            )
            if self._uses_default_downloader_factory:
                service.download(*callbacks, lambda completed: (setattr(service, '_last_completion', completed)))
            else:
                service.download(*callbacks)
            latest = self._store.get(item.id)
            if latest:
                last_completion = getattr(service, '_last_completion', None)
                if last_completion:
                    completed_download = last_completion
        except Exception as exc:
            final_status = "failed"
            error = str(exc)
            self._log_callback(f"Queue item failed for {item.url}: {error}")

        with self._lock:
            try:
                latest = self._store.get(item.id)
                if latest:
                    progress = 100 if final_status == "completed" else latest.progress
                    updated = replace(latest, status=final_status, progress=progress, speed="", error=error)
                    if completed_download:
                        path = Path(completed_download.output_path)
                        if not path.is_absolute():
                            path = Path(item.download_dir).expanduser() / path
                        updated = replace(
                            updated,
                            name=completed_download.title or path.name,
                            output_path=str(path),
                            extractor=completed_download.extractor,
                            media_id=completed_download.media_id,
                            completed_at=datetime.now(timezone.utc).isoformat(timespec="seconds"),
                        )
                    self._store.replace(updated)
            finally:
                self._running.discard(item.id)
                self._events.put(QueueEvent("item", item.id))
        self._schedule()

    def _handle_status(self, item_id: str, phase: str, event: StatusEvent | str) -> None:
        item = self._store.get(item_id)
        if not item:
            return
        if phase == "speed":
            updated = replace(item, speed=str(event) if isinstance(event, str) else "")
        elif phase == "name":
            updated = replace(item, name=str(event) if isinstance(event, str) else item.name)
        else:
            progress = None
            if not isinstance(event, str):
                progress = event_percent(event)
            elif event:
                progress = _percent_from_message(event)
            updated = replace(item, progress=progress)
        try:
            self._store.replace(updated)
        finally:
            self._events.put(QueueEvent("item", item_id))

    def _handle_log(self, item_id: str, message: str) -> None:
        self._log_callback(message)
        name = _name_from_output(message)
        output_path = _output_path_from_output(message)
        if not name and not output_path:
            return
        item = self._store.get(item_id)
        if not item:
            return
        updated = item
        if name:
            updated = replace(updated, name=name)
        if output_path:
            path = Path(output_path)
            if not path.is_absolute():
                path = Path(item.download_dir).expanduser() / path
            updated = replace(updated, output_path=str(path))
        try:
            self._store.replace(updated)
        finally:
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
            download_dir=str(raw_item["download_dir"]),
            filename_template=str(raw_item["filename_template"]),
            organize_by_channel=bool(raw_item.get("organize_by_channel", True)),
            added_at=str(raw_item["added_at"]),
            category_id=str(raw_item.get("category_id", "default")) or "default",
            category_name=str(raw_item.get("category_name", "Default")) or "Default",
            status=status,
            name=str(raw_item.get("name", "")),
            progress=_optional_int(raw_item.get("progress")),
            speed=str(raw_item.get("speed", "")),
            error=str(raw_item.get("error", "")),
            output_path=str(raw_item.get("output_path", "")),
            previous_output_path=str(raw_item.get("previous_output_path", "")),
            warning=str(raw_item.get("warning", "")),
            source_type=str(raw_item.get("source_type", "manual")),
            playlist_id=str(raw_item.get("playlist_id", "")),
            playlist_position=(int(raw_item["playlist_position"]) if raw_item.get("playlist_position") is not None else None),
            playlist_title=str(raw_item.get("playlist_title", "")),
            extractor=str(raw_item.get("extractor", "")),
            media_id=str(raw_item.get("media_id", "")),
            completed_at=str(raw_item.get("completed_at", "")),
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
    output_path = _output_path_from_output(line)
    return Path(output_path).name if output_path else ""


def _output_path_from_output(line: str) -> str:
    markers = ("[download] Destination: ", "[ExtractAudio] Destination: ")
    for marker in markers:
        if marker in line:
            return line.split(marker, 1)[1].strip().strip('"')
    merger_match = re.search(r'\[Merger\]\s+Merging formats into\s+"(.+)"', line)
    if merger_match:
        return merger_match.group(1)
    return ""


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
