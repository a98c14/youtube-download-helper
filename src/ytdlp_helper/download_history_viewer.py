from __future__ import annotations

from .database import Database, DownloadRecord


class DownloadHistoryViewer:
    def __init__(self, database: Database) -> None:
        self._database = database

    def get_history(self) -> list[DownloadRecord]:
        return self._database.download_history()
