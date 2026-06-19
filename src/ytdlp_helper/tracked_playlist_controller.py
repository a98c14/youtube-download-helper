from __future__ import annotations

import threading
from typing import Callable

from .config import AppPaths, DEFAULT_FILENAME_TEMPLATE
from .database import Database, PlaylistCandidate
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
    def __init__(self, database: Database, paths: AppPaths) -> None:
        self._database = database
        self._paths = paths
        self.check_running = False

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
        self._database.reset_tracker(tracker_id)

    def update_tracker(self, tracker_id: int, preset: str, category_id: str) -> None:
        self._database.update_tracker(tracker_id, preset=preset, category_id=category_id)

    def check_all(self, language: str, on_complete: Callable) -> None:
        if self.check_running:
            return
        self.check_running = True

        def work() -> None:
            checker = PlaylistChecker(self._paths)
            counts: list[tuple[str, int, str]] = []
            for tracker in self._database.trackers():
                if not tracker.active:
                    continue
                try:
                    title, entries = checker.check(tracker.url)
                    self._database.record_playlist_check(tracker.id, entries, playlist_title=title)
                    counts.append((title or tracker.title or tracker.playlist_id, len(entries), ""))
                except Exception as exc:  # noqa: BLE001
                    self._database.record_playlist_check(tracker.id, None, str(exc))
                    counts.append((tracker.title or tracker.playlist_id, 0, str(exc)))
            on_complete(counts)

        threading.Thread(target=work, daemon=True).start()

    def pending_candidates(self) -> list[PlaylistCandidate]:
        return self._database.pending_candidates()

    def decide_entries(self, entry_ids: list[int], decision: str) -> None:
        self._database.decide_entries(entry_ids, decision)

    def queue_rows(
        self, candidates: list[PlaylistCandidate], filename_template: str,
    ) -> list[dict[str, object]]:
        trackers = {tracker.id: tracker for tracker in self._database.trackers()}
        categories = {category.id: category for category in self._database.categories()}
        rows = []
        for candidate in candidates:
            tracker = trackers[candidate.playlist_id]
            category = categories[tracker.category_id]
            template = filename_template.strip() or DEFAULT_FILENAME_TEMPLATE
            if candidate.position is not None:
                template = f"{candidate.position} - {template}"
            rows.append({
                "url": f"https://www.youtube.com/watch?v={candidate.video_id}",
                "preset": tracker.preset,
                "download_dir": category.download_dir,
                "filename_template": template,
                "category_id": category.id,
                "category_name": category.name,
                "source_type": "tracker",
                "playlist_id": tracker.playlist_id,
                "playlist_position": candidate.position,
                "playlist_title": tracker.title,
            })
        return rows

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

    def check_summary(self, language: str, counts: list[tuple[str, int, str]]) -> str:
        from .i18n import translate
        lines = []
        for name, count, error in counts:
            entry_key = "tracker.check.failed" if error else "tracker.check.current"
            lines.append(translate(language, entry_key, name=name, count=count, error=error))
        return "\n".join(lines)
