from __future__ import annotations

from collections.abc import Callable
from datetime import datetime
from pathlib import Path

from .config import AppPaths


DEFAULT_MAX_LOG_BYTES = 5 * 1024 * 1024


class ActivityLogStore:
    def __init__(
        self,
        paths: AppPaths,
        max_bytes: int = DEFAULT_MAX_LOG_BYTES,
        now: Callable[[], datetime] | None = None,
    ) -> None:
        self._paths = paths
        self._max_bytes = max_bytes
        self._now = now or datetime.now
        self.current_session_lines: list[str] = []

    @property
    def active_log_file(self) -> Path:
        return self._paths.activity_log_file

    def append(self, message: str) -> str | None:
        stripped = message.strip()
        if not stripped:
            return None

        timestamped_line = f"[{self._now():%Y-%m-%d %H:%M:%S}] {stripped}"
        self._rotate_if_needed()
        self._paths.logs_dir.mkdir(parents=True, exist_ok=True)
        with self.active_log_file.open("a", encoding="utf-8") as log_file:
            log_file.write(timestamped_line + "\n")
        self.current_session_lines.append(timestamped_line)
        return timestamped_line

    def read_all_lines(self) -> list[str]:
        lines: list[str] = []
        for log_file in self.iter_log_files():
            try:
                lines.extend(log_file.read_text(encoding="utf-8").splitlines())
            except OSError:
                continue
        return lines

    def iter_log_files(self) -> list[Path]:
        if not self._paths.logs_dir.exists():
            return []

        rotated_files = sorted(
            path for path in self._paths.logs_dir.glob("activity-*.log") if path.is_file()
        )
        files = list(rotated_files)
        if self.active_log_file.exists():
            files.append(self.active_log_file)
        return files

    def _rotate_if_needed(self) -> None:
        if not self.active_log_file.exists():
            return
        if self.active_log_file.stat().st_size < self._max_bytes:
            return

        self._paths.logs_dir.mkdir(parents=True, exist_ok=True)
        rotated_file = self._next_rotated_log_file()
        self.active_log_file.replace(rotated_file)

    def _next_rotated_log_file(self) -> Path:
        timestamp = self._now().strftime("%Y%m%d-%H%M%S")
        candidate = self._paths.logs_dir / f"activity-{timestamp}.log"
        if not candidate.exists():
            return candidate

        index = 1
        while True:
            candidate = self._paths.logs_dir / f"activity-{timestamp}-{index}.log"
            if not candidate.exists():
                return candidate
            index += 1
