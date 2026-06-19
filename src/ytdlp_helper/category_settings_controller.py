from __future__ import annotations

from dataclasses import replace
from pathlib import Path
from typing import Iterable

from .config import (
    Category,
    DEFAULT_FILENAME_TEMPLATE,
    MAX_QUEUE_CONCURRENCY,
    MIN_QUEUE_CONCURRENCY,
    Settings,
    load_settings,
    save_settings,
    settings_categories,
)
from .database import Database
from .i18n import language_options, normalize_language
from .dependencies import RuntimeToolResolver


class CategorySettingsController:
    def __init__(self, database: Database) -> None:
        self._database = database

    def load_categories(self) -> list[Category]:
        return self._database.categories() or []

    def selected_category(self, categories: list[Category], selected_id: str) -> Category:
        for category in categories:
            if category.id == selected_id:
                return category
        return categories[0]

    def validate_download_folder(self, value: str) -> Path | None:
        raw_path = value.strip()
        if not raw_path:
            return None
        return Path(raw_path).expanduser()

    def validate_filename_template(self, value: str) -> str | None:
        filename_template = value.strip()
        if not filename_template:
            return None
        if "%(ext)s" not in filename_template:
            return None
        if "/" in filename_template or "\\" in filename_template:
            return None
        return filename_template

    def validate_queue_concurrency(self, value: object) -> int:
        try:
            concurrency = int(value)
        except (TypeError, ValueError):
            return 1
        return max(MIN_QUEUE_CONCURRENCY, min(MAX_QUEUE_CONCURRENCY, concurrency))

    def validate_categories(self, categories: list[Category]) -> list[Category] | None:
        if not categories:
            return None
        validated: list[Category] = []
        for category in categories:
            if not category.name.strip():
                return None
            folder = self.validate_download_folder(category.download_dir)
            if not folder:
                return None
            folder.mkdir(parents=True, exist_ok=True)
            validated.append(Category(category.id, category.name.strip(), str(folder)))
        return validated

    def persist_settings(
        self,
        paths: object,
        settings: Settings,
        categories: Iterable[Category] | None,
    ) -> None:
        from .config import AppPaths

        if categories is not None:
            self._database.replace_categories(categories)
        save_settings(paths, settings)

    def language_code_for_label(self, pairs: list[tuple[str, str]], label: str) -> str | None:
        for language_label, language_code in pairs:
            if language_label == label:
                return language_code
        return None

    def language_label_for_code(self, pairs: list[tuple[str, str]], code: str) -> str:
        for label, language_code in pairs:
            if language_code == code:
                return label
        return pairs[0][0]
