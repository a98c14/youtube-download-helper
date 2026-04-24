from __future__ import annotations

from .app_update import AppUpdateResult, check_and_stage_app_update
from .config import AppPaths
from .dependencies import LogCallback, StatusCallback, refresh_runtime_tools


class UpdateService:
    def __init__(self, paths: AppPaths) -> None:
        self._paths = paths

    def update(self, log_callback: LogCallback, status_callback: StatusCallback) -> AppUpdateResult:
        refresh_runtime_tools(self._paths, log_callback, status_callback)
        return check_and_stage_app_update(log_callback, status_callback)
