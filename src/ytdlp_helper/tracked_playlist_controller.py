from __future__ import annotations

import threading
from typing import Callable

from .config import AppPaths, DEFAULT_FILENAME_TEMPLATE
from .database import Database
from .download_queue import QueueItem, QueueStore
from .playlist_tracker import PlaylistChecker, canonical_playlist_url, parse_youtube_playlist_id


PRESET_KEYS = [
    "best-video",
    "video-1080p",
    "video-720p",
    "video-480p",
    "audio-mp3",
    "audio-m4a",
]


class TrackedPlaylistController:
    def __init__(self, database: Database, paths: AppPaths, queue_store: QueueStore | None = None) -> None:
        self._database = database
        self._paths = paths
        self._queue_store = queue_store
        self.check_running = False

    def set_queue_store(self, queue_store: QueueStore) -> None:
        self._queue_store = queue_store

    def add_tracker(self, url: str, preset: str, category_id: str) -> int:
        playlist_id = parse_youtube_playlist_id(url)
        if not playlist_id:
            raise ValueError("Invalid YouTube playlist URL")
        return self._database.add_tracker(
            playlist_id, canonical_playlist_url(playlist_id),
            playlist_id, preset, category_id,
        )

    def toggle_active(self, tracker_id: int, active: bool) -> None:
        self._database.set_tracker_active(tracker_id, active)

    def reset_tracker(self, tracker_id: int) -> None:
        self._database.record_tracker_check(tracker_id, entry_count=0, new_count=0)

    def update_tracker(self, tracker_id: int, preset: str, category_id: str) -> None:
        self._database.update_tracker(tracker_id, preset=preset, category_id=category_id)

    def check_all(self, language: str, on_complete: Callable) -> None:
        if self.check_running:
            return
        self.check_running = True

        def work() -> None:
            checker = PlaylistChecker(self._paths)
            counts: list[tuple[str, int, str, int]] = []
            for tracker in self._database.trackers():
                if not tracker.active:
                    continue
                try:
                    title, entries = checker.check(tracker.url)
                    new_count = self._create_queue_items(tracker, entries, title)
                    self._database.record_tracker_check(
                        tracker.id, entry_count=len(entries), new_count=new_count,
                        playlist_title=title,
                    )
                    counts.append((title or tracker.title or tracker.playlist_id, len(entries), "", new_count))
                except Exception as exc:
                    self._database.record_tracker_check(tracker.id, error=str(exc))
                    counts.append((tracker.title or tracker.playlist_id, 0, str(exc), 0))
            on_complete(counts)

        threading.Thread(target=work, daemon=True).start()

    def _create_queue_items(self, tracker: object, entries: list[dict[str, object]],
                            playlist_title: str) -> int:
        if self._queue_store is None:
            return 0
        categories = {category.id: category for category in self._database.categories()}
        category = categories.get(tracker.category_id)
        if category is None:
            return 0
        new_count = 0
        for entry in entries:
            video_id = str(entry["video_id"])
            existing = self._queue_store.find_existing(
                video_id, tracker.preset, category.download_dir,
                DEFAULT_FILENAME_TEMPLATE, True, tracker.playlist_id,
            )
            if existing:
                continue
            new_count += 1
            template = DEFAULT_FILENAME_TEMPLATE
            position = entry.get("position")
            if position is not None:
                template = f"{position} - {template}"
            self._queue_store.add(
                url=f"https://www.youtube.com/watch?v={video_id}",
                preset=tracker.preset,
                download_dir=category.download_dir,
                filename_template=template,
                organize_by_channel=True,
                category_id=category.id,
                category_name=category.name,
                source_type="tracker",
                playlist_id=tracker.playlist_id,
                playlist_position=position,
                playlist_title=playlist_title or tracker.title,
                media_id=video_id,
                extractor="youtube",
            )
        return new_count

    def preset_labels_for_language(self, language: str) -> list[str]:
        from .i18n import translate
        return [translate(language, f"preset.{key}") for key in PRESET_KEYS]

    def preset_label_for_language(self, language: str, key: str) -> str:
        from .i18n import translate
        return translate(language, f"preset.{key}")

    def preset_key_for_label(self, language: str, label: str) -> str | None:
        for key in PRESET_KEYS:
            if self.preset_label_for_language(language, key) == label:
                return key
        return None

    def state_label(self, language: str, active: bool) -> str:
        from .i18n import translate
        return translate(language, "tracker.state.active" if active else "tracker.state.stopped")

    def outcome_label(self, language: str, outcome: str) -> str:
        from .i18n import translate
        key = outcome if outcome in {"success", "failed"} else "not_checked"
        return translate(language, f"tracker.outcome.{key}")

    def check_summary(self, language: str, counts: list[tuple[str, int, str, int]]) -> str:
        from .i18n import translate
        lines = []
        for name, count, error, _new_count in counts:
            entry_key = "tracker.check.failed" if error else "tracker.check.current"
            lines.append(translate(language, entry_key, name=name, count=count, error=error))
        return "\n".join(lines)
