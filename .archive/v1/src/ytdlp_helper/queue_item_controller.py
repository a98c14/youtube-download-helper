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

    def find_existing(self, media_id: str, preset: str, download_dir: str,
                      filename_template: str, organize_by_channel: bool,
                      playlist_id: str) -> QueueItem | None:
        return self._store.find_existing(
            media_id, preset, download_dir, filename_template,
            organize_by_channel, playlist_id,
        )

    def add_item(
        self,
        url: str,
        preset: str,
        download_dir: str,
        filename_template: str,
        category_id: str,
        category_name: str,
        *,
        organize_by_channel: bool = True,
        source_type: str = "manual",
        playlist_id: str = "",
        playlist_position: int | None = None,
        playlist_title: str = "",
        media_id: str = "",
        extractor: str = "",
    ) -> QueueItem:
        return self._store.add(
            url, preset, download_dir,
            filename_template.strip() or DEFAULT_FILENAME_TEMPLATE,
            organize_by_channel,
            category_id, category_name,
            source_type=source_type,
            playlist_id=playlist_id,
            playlist_position=playlist_position,
            playlist_title=playlist_title,
            media_id=media_id,
            extractor=extractor,
        )

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
