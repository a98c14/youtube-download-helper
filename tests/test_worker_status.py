from __future__ import annotations

from pathlib import Path
import sys
import threading
import time
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from ytdlp_helper.app_update import AppUpdateResult
from ytdlp_helper.i18n import translate
from ytdlp_helper.worker_status import (
    AppUpdatePhase,
    AppUpdateStatus,
    DownloadPhase,
    DownloadStatus,
    RuntimeTool,
    RuntimeToolPhase,
    RuntimeToolStatus,
    StatusEvent,
    WorkerReporter,
    WorkerStatusPipeline,
    WorkerTask,
    WorkerPhase,
)


class FakeUi:
    def __init__(self) -> None:
        self.busy: list[bool] = []
        self.statuses: list[tuple[WorkerPhase, str]] = []
        self.status_keys: list[tuple[WorkerPhase, str, dict[str, object]]] = []
        self.speeds: list[str | None] = []
        self.progress: list[int] = []
        self.logs: list[str] = []
        self.infos: list[tuple[str, str, bool]] = []
        self.errors: list[tuple[str, str]] = []
        self.confirms: list[tuple[str, str]] = []
        self.confirm_result = False
        self.restarts: list[Path] = []
        self.version_refreshes = 0

    def set_busy(self, busy: bool) -> None:
        self.busy.append(busy)

    def show_status(self, phase: WorkerPhase, event: StatusEvent | str) -> None:
        self.statuses.append((phase, event))

    def show_status_key(self, phase: WorkerPhase, key: str, **params: object) -> None:
        self.status_keys.append((phase, key, params))

    def show_speed(self, speed: str | None) -> None:
        self.speeds.append(speed)

    def show_progress(self, value: int) -> None:
        self.progress.append(value)

    def append_log(self, message: str) -> None:
        self.logs.append(message)

    def info(self, title_key: str, message: str, *, localized: bool = False) -> None:
        self.infos.append((title_key, message, localized))

    def error(self, title_key: str, message: str) -> None:
        self.errors.append((title_key, message))

    def confirm(self, title_key: str, message_key: str) -> bool:
        self.confirms.append((title_key, message_key))
        return self.confirm_result

    def restart(self, script_path: Path) -> None:
        self.restarts.append(script_path)

    def refresh_runtime_version_cache(self) -> None:
        self.version_refreshes += 1


class WorkerStatusPipelineTests(unittest.TestCase):
    def test_download_task_drains_status_log_and_completion(self) -> None:
        ui = FakeUi()
        pipeline = _pipeline(ui)

        started = pipeline.start(
            WorkerTask[None](
                kind="download",
                initial_status_key=None,
                initial_log="Starting download for https://example.test/video",
                run=lambda reporter: _report_download_success(reporter),
                success=lambda _result, worker_ui: (
                    worker_ui.show_status_key("completed", "status.download_completed"),
                    worker_ui.info("dialog.download_finished.title", "status.download_completed", localized=True),
                ),
                error_title_key="dialog.download_failed.title",
            )
        )
        _drain_until_idle(pipeline)

        self.assertTrue(started)
        self.assertEqual(ui.busy[0], True)
        self.assertEqual(ui.busy[-1], False)
        self.assertEqual(ui.logs[:2], ["", "Starting download for https://example.test/video"])
        self.assertIn(("resolving", DownloadStatus(DownloadPhase.RESOLVING_VIDEO)), ui.statuses)
        self.assertIn(("downloading", DownloadStatus(DownloadPhase.DOWNLOADING, percent=42)), ui.statuses)
        self.assertIn("yt-dlp output line", ui.logs)
        self.assertIn(42, ui.progress)
        self.assertIn(("completed", "status.download_completed", {}), ui.status_keys)
        self.assertEqual(ui.infos, [("dialog.download_finished.title", "status.download_completed", True)])

    def test_busy_pipeline_rejects_second_task_without_mutating_ui(self) -> None:
        ui = FakeUi()
        pipeline = _pipeline(ui)
        release = threading.Event()
        task = WorkerTask[None](
            kind="download",
            initial_status_key=None,
            initial_log="first",
            run=lambda _reporter: release.wait(timeout=2),
            success=lambda _result, _ui: None,
            error_title_key="dialog.download_failed.title",
        )

        self.assertTrue(pipeline.start(task))
        logs_after_first_start = list(ui.logs)
        self.assertFalse(pipeline.start(task))
        self.assertEqual(ui.logs, logs_after_first_start)

        release.set()
        _drain_until_idle(pipeline)
        self.assertEqual(ui.busy[-1], False)

    def test_runtime_tool_status_translates_via_typed_event(self) -> None:
        ui = FakeUi()
        pipeline = WorkerStatusPipeline(ui, lambda key, **params: translate("tr", key, **params), _noop_after)

        pipeline.start(
            WorkerTask[None](
                kind="download",
                initial_status_key=None,
                initial_log="download",
                run=lambda reporter: reporter.status(
                    "installing",
                    RuntimeToolStatus(RuntimeTool.FFMPEG, RuntimeToolPhase.DOWNLOADING, percent=42),
                ),
                success=lambda _result, _ui: None,
                error_title_key="dialog.download_failed.title",
            )
        )
        _drain_until_idle(pipeline)

        self.assertIn(
            ("installing", RuntimeToolStatus(RuntimeTool.FFMPEG, RuntimeToolPhase.DOWNLOADING, percent=42)),
            ui.statuses,
        )
        self.assertIn(42, ui.progress)

    def test_failure_routes_to_task_error_title_and_reenables_actions(self) -> None:
        ui = FakeUi()
        pipeline = _pipeline(ui)

        pipeline.start(
            WorkerTask[None](
                kind="download",
                initial_status_key=None,
                initial_log="download",
                run=lambda _reporter: (_ for _ in ()).throw(RuntimeError("network down")),
                success=lambda _result, _ui: None,
                error_title_key="dialog.download_failed.title",
            )
        )
        _drain_until_idle(pipeline)

        self.assertIn(("failed", "network down"), ui.statuses)
        self.assertEqual(ui.errors, [("dialog.download_failed.title", "network down")])
        self.assertEqual(ui.progress[-1], 0)
        self.assertEqual(ui.busy[-1], False)

    def test_update_result_without_restart_shows_finished_info(self) -> None:
        ui = FakeUi()
        pipeline = _pipeline(ui)

        pipeline.start(
            WorkerTask[AppUpdateResult](
                kind="update",
                initial_status_key="status.updating_runtime_tools",
                initial_log="Starting update",
                run=lambda _reporter: AppUpdateResult("Runtime tools updated. App is already current."),
                success=_update_success,
                error_title_key="dialog.update_failed.title",
            )
        )
        _drain_until_idle(pipeline)

        self.assertEqual(ui.version_refreshes, 1)
        self.assertIn(("completed", "Runtime tools updated. App is already current."), ui.statuses)
        self.assertIn(100, ui.progress)
        self.assertEqual(ui.infos, [("dialog.update_finished.title", "Runtime tools updated. App is already current.", False)])

    def test_update_result_with_restart_can_restart_or_defer(self) -> None:
        script_path = Path("C:/tmp/apply-update.ps1")
        for confirm_result, expected_restart_count, expected_info_count in ((True, 1, 0), (False, 0, 1)):
            with self.subTest(confirm_result=confirm_result):
                ui = FakeUi()
                ui.confirm_result = confirm_result
                pipeline = _pipeline(ui)

                pipeline.start(
                    WorkerTask[AppUpdateResult](
                        kind="update",
                        initial_status_key="status.updating_runtime_tools",
                        initial_log="Starting update",
                        run=lambda _reporter: AppUpdateResult("Ready.", script_path),
                        success=_update_success,
                        error_title_key="dialog.update_failed.title",
                    )
                )
                _drain_until_idle(pipeline)

                self.assertEqual(ui.confirms, [("dialog.restart_to_update.title", "dialog.restart_to_update.message")])
                self.assertEqual(len(ui.restarts), expected_restart_count)
                self.assertEqual(len(ui.infos), expected_info_count)


def _report_download_success(reporter: WorkerReporter) -> None:
    reporter.status("resolving", DownloadStatus(DownloadPhase.RESOLVING_VIDEO))
    reporter.status("downloading", DownloadStatus(DownloadPhase.DOWNLOADING, percent=42))
    reporter.log("yt-dlp output line")


def _update_success(result: AppUpdateResult, ui: FakeUi) -> None:
    ui.refresh_runtime_version_cache()
    ui.show_status("completed", result.message)
    ui.show_progress(100)
    ui.show_speed(None)
    if result.restart_ready and result.restart_script:
        if ui.confirm("dialog.restart_to_update.title", "dialog.restart_to_update.message"):
            ui.show_status_key("completed", "status.ready_to_restart")
            ui.restart(result.restart_script)
        else:
            ui.info("dialog.update_ready.title", "dialog.update_ready.message", localized=True)
    else:
        ui.info("dialog.update_finished.title", result.message)


def _pipeline(ui: FakeUi) -> WorkerStatusPipeline:
    return WorkerStatusPipeline(ui, lambda key, **params: translate("en", key, **params), _noop_after)


def _noop_after(_ms: int, _callback: object) -> None:
    return None


def _drain_until_idle(pipeline: WorkerStatusPipeline) -> None:
    for _ in range(100):
        pipeline.poll()
        if not pipeline.is_busy:
            return
        time.sleep(0.01)
    raise AssertionError("pipeline did not become idle")


if __name__ == "__main__":
    unittest.main()
