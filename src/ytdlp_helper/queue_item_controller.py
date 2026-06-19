from __future__ import annotations

from pathlib import Path

from .config import DEFAULT_FILENAME_TEMPLATE
from .download_queue import QueueItem, QueueRunner, QueueStore


class QueueItemController:
    def __init__(self, store: QueueStore, runner: QueueRunner) -> None:
        self._store = store
        self._runner = runner

    @property
    def store(self) -> QueueStore:
        return self._store

    @property
    def runner(self) -> QueueRunner:
        return self._runner

    def items(self) -> list[QueueItem]:
        return self._store.items()

    def get_item(self, item_id: str) -> QueueItem | None:
        return self._store.get(item_id)

    def has_duplicate_url(self, url: str) -> bool:
        return self._store.has_duplicate_url(url)

    def add_item(
        self,
        url: str,
        preset: str,
        playlist: bool,
        download_dir: str,
        filename_template: str,
        category_id: str,
        category_name: str,
    ) -> QueueItem:
        return self._store.add(
            url, preset, playlist, download_dir,
            filename_template.strip() or DEFAULT_FILENAME_TEMPLATE,
            category_id, category_name,
        )

    def add_many(self, items: list[dict[str, object]], entry_ids: list[int] | None = None) -> list[QueueItem]:
        return self._store.add_many(items, entry_ids)

    def retry(self, item_id: str) -> bool:
        return self._store.retry(item_id)

    def remove(self, item_id: str) -> bool:
        return self._store.remove(item_id)

    def move(self, item_id: str, direction: int) -> bool:
        return self._store.move(item_id, direction)

    def clear_completed(self) -> None:
        self._store.clear_completed()

    def items_matching_filter(self, filter_key: str) -> list[QueueItem]:
        all_items = self._store.items()
        if filter_key == "ongoing":
            return [item for item in all_items if item.status in {"queued", "running"}]
        if filter_key in {"queued", "completed", "failed"}:
            return [item for item in all_items if item.status == filter_key]
        return all_items

    def notify_changed(self) -> None:
        self._runner.notify_queue_changed()

    def resume(self, concurrency: int) -> None:
        self._runner.resume(concurrency)

    def pause(self) -> None:
        self._runner.pause()
