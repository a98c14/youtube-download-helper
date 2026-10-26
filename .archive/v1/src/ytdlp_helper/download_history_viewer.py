from __future__ import annotations

from .database import Database, QueueHistoryRecord


class DownloadHistoryViewer:
    def __init__(self, database: Database) -> None:
        self._database = database

    def get_history(self) -> list[QueueHistoryRecord]:
        return self._database.queue_history()
