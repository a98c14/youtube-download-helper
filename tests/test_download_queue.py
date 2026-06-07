from __future__ import annotations

from dataclasses import replace
import json
import sys
import tempfile
import time
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from ytdlp_helper.config import AppPaths
from ytdlp_helper.download_queue import QueueItem, QueueRunner, QueueStore


class QueueStoreTests(unittest.TestCase):
    def test_load_recovers_running_items_as_failed(self) -> None:
        paths = _paths()
        store = QueueStore.for_paths(paths)
        paths.data_dir.mkdir(parents=True)
        store.path.write_text(
            json.dumps(
                {
                    "version": 1,
                    "items": [
                        _raw_item("one", "running"),
                        _raw_item("two", "queued"),
                    ],
                }
            ),
            encoding="utf-8",
        )

        items = store.load()

        self.assertEqual([item.status for item in items], ["failed", "queued"])
        self.assertEqual(items[0].error, "Interrupted while app was closed.")

    def test_load_skips_invalid_rows_and_corrupt_data(self) -> None:
        paths = _paths()
        store = QueueStore.for_paths(paths)
        paths.data_dir.mkdir(parents=True)
        store.path.write_text(
            json.dumps({"version": 1, "items": [_raw_item("ok", "queued"), {"id": "bad"}]}),
            encoding="utf-8",
        )

        self.assertEqual([item.id for item in store.load()], ["ok"])
        self.assertEqual(store.get("ok").output_path, "")  # type: ignore[union-attr]

        store.path.write_text("{", encoding="utf-8")
        self.assertEqual(store.load(), [])

    def test_save_add_move_retry_remove_and_clear_completed(self) -> None:
        store = QueueStore.for_paths(_paths())
        first = store.add("https://example.test/1", "best-video", False, "downloads", "%(title)s.%(ext)s")
        second = store.add("https://example.test/2", "audio-mp3", True, "downloads", "%(id)s.%(ext)s")

        self.assertTrue(store.has_duplicate_url("https://example.test/1"))
        self.assertTrue(store.move(second.id, -1))
        self.assertEqual([item.id for item in store.items()], [second.id, first.id])

        store.replace(replace(first, status="failed", error="network"))
        self.assertTrue(store.retry(first.id))
        self.assertEqual(store.get(first.id).status, "queued")  # type: ignore[union-attr]
        self.assertEqual(store.get(first.id).error, "")  # type: ignore[union-attr]

        store.replace(replace(first, status="failed", output_path=str(Path("downloads") / "old.mp4")))
        self.assertTrue(store.retry(first.id))
        self.assertEqual(store.get(first.id).output_path, "")  # type: ignore[union-attr]

        store.replace(replace(second, status="completed"))
        store.clear_completed()
        self.assertEqual([item.id for item in store.items()], [first.id])
        self.assertTrue(store.remove(first.id))
        self.assertEqual(store.items(), [])


class QueueRunnerTests(unittest.TestCase):
    def test_resume_runs_in_order_and_respects_concurrency(self) -> None:
        paths = _paths()
        store = QueueStore.for_paths(paths)
        first = store.add("https://example.test/1", "best-video", False, str(paths.download_dir), "%(title)s.%(ext)s")
        second = store.add("https://example.test/2", "best-video", False, str(paths.download_dir), "%(title)s.%(ext)s")
        service = ControlledService()
        runner = QueueRunner(store, paths, lambda *_args: None, lambda *_args: service)

        runner.resume(1)
        _wait_for(lambda: service.started == [first.url])
        self.assertEqual(store.get(first.id).status, "running")  # type: ignore[union-attr]
        self.assertEqual(store.get(second.id).status, "queued")  # type: ignore[union-attr]

        service.release_one()
        _wait_for(lambda: service.started == [first.url, second.url])
        service.release_one()
        _wait_for(lambda: store.get(second.id).status == "completed")  # type: ignore[union-attr]

        self.assertEqual(store.get(first.id).progress, 100)  # type: ignore[union-attr]
        self.assertEqual(store.get(second.id).progress, 100)  # type: ignore[union-attr]

    def test_pause_allows_current_item_to_finish_without_starting_next(self) -> None:
        paths = _paths()
        store = QueueStore.for_paths(paths)
        first = store.add("https://example.test/1", "best-video", False, str(paths.download_dir), "%(title)s.%(ext)s")
        second = store.add("https://example.test/2", "best-video", False, str(paths.download_dir), "%(title)s.%(ext)s")
        service = ControlledService()
        runner = QueueRunner(store, paths, lambda *_args: None, lambda *_args: service)

        runner.resume(1)
        _wait_for(lambda: store.get(first.id).status == "running")  # type: ignore[union-attr]
        runner.pause()
        service.release_one()
        _wait_for(lambda: store.get(first.id).status == "completed")  # type: ignore[union-attr]

        self.assertEqual(store.get(second.id).status, "queued")  # type: ignore[union-attr]

    def test_not_yet_started_items_use_latest_organization_setting(self) -> None:
        paths = _paths()
        store = QueueStore.for_paths(paths)
        first = store.add("https://example.test/1", "best-video", False, str(paths.download_dir), "%(title)s.%(ext)s")
        second = store.add("https://example.test/2", "best-video", False, str(paths.download_dir), "%(title)s.%(ext)s")
        service = ControlledService()
        organize_by_channel = True
        starts: list[tuple[str, bool]] = []

        def factory(_paths: object, _template: str, organize: bool) -> ControlledService:
            starts.append((store.items()[len(starts)].url, organize))
            return service

        runner = QueueRunner(
            store,
            paths,
            lambda *_args: None,
            factory,
            organize_by_channel_provider=lambda: organize_by_channel,
        )

        runner.resume(1)
        _wait_for(lambda: service.started == [first.url])
        organize_by_channel = False
        service.release_one()
        _wait_for(lambda: service.started == [first.url, second.url])
        service.release_one()
        _wait_for(lambda: store.get(second.id).status == "completed")  # type: ignore[union-attr]

        self.assertEqual(starts, [(first.url, True), (second.url, False)])

    def test_failure_does_not_block_following_items(self) -> None:
        paths = _paths()
        store = QueueStore.for_paths(paths)
        first = store.add("https://example.test/fail", "best-video", False, str(paths.download_dir), "%(title)s.%(ext)s")
        second = store.add("https://example.test/ok", "best-video", False, str(paths.download_dir), "%(title)s.%(ext)s")
        service = ControlledService(fail_urls={first.url})
        logs: list[str] = []
        runner = QueueRunner(store, paths, logs.append, lambda *_args: service)

        runner.resume(1)
        service.release_one()
        _wait_for(lambda: store.get(second.id).status == "running")  # type: ignore[union-attr]
        service.release_one()
        _wait_for(lambda: store.get(second.id).status == "completed")  # type: ignore[union-attr]

        self.assertEqual(store.get(first.id).status, "failed")  # type: ignore[union-attr]
        self.assertIn("Queue item failed", "\n".join(logs))

    def test_archive_skip_status_is_preserved(self) -> None:
        paths = _paths()
        store = QueueStore.for_paths(paths)
        item = store.add("https://example.test/skip", "best-video", False, str(paths.download_dir), "%(title)s.%(ext)s")
        service = ControlledService(skip_urls={item.url})
        runner = QueueRunner(store, paths, lambda *_args: None, lambda *_args: service)

        runner.resume(1)
        service.release_one()
        _wait_for(lambda: store.get(item.id).status == "skipped")  # type: ignore[union-attr]

        self.assertEqual(store.get(item.id).progress, None)  # type: ignore[union-attr]

    def test_output_path_is_captured_from_download_logs(self) -> None:
        paths = _paths()
        store = QueueStore.for_paths(paths)
        item = store.add("https://example.test/ok", "best-video", False, str(paths.download_dir), "%(title)s.%(ext)s")
        service = ControlledService(final_path=paths.download_dir / "ok.mp4")
        runner = QueueRunner(store, paths, lambda *_args: None, lambda *_args: service)

        runner.resume(1)
        service.release_one()
        _wait_for(lambda: store.get(item.id).status == "completed")  # type: ignore[union-attr]

        self.assertEqual(store.get(item.id).name, "ok.mp4")  # type: ignore[union-attr]
        self.assertEqual(store.get(item.id).output_path, str(paths.download_dir / "ok.mp4"))  # type: ignore[union-attr]


class ControlledService:
    def __init__(
        self,
        fail_urls: set[str] | None = None,
        skip_urls: set[str] | None = None,
        final_path: Path | None = None,
    ) -> None:
        self.started: list[str] = []
        self._releases: list[bool] = []
        self._fail_urls = fail_urls or set()
        self._skip_urls = skip_urls or set()
        self._final_path = final_path

    def download(self, request: object, status_callback: object, log_callback: object) -> None:
        url = request.url  # type: ignore[attr-defined]
        self.started.append(url)
        if self._final_path:
            log_callback(f'[Merger] Merging formats into "{self._final_path}"')
        else:
            log_callback(f"[download] Destination: {url.rsplit('/', 1)[-1]}.mp4")
        status_callback("downloading", "Downloading 50%")
        while len(self._releases) < len(self.started):
            time.sleep(0.01)
        if url in self._skip_urls:
            status_callback("skipped", "Already downloaded; skipped by archive")
            return
        if url in self._fail_urls:
            raise RuntimeError("network down")

    def release_one(self) -> None:
        self._releases.append(True)


def _wait_for(predicate: object) -> None:
    for _ in range(200):
        if predicate():
            return
        time.sleep(0.01)
    raise AssertionError("condition was not met")


def _raw_item(item_id: str, status: str) -> dict[str, object]:
    return {
        "id": item_id,
        "url": f"https://example.test/{item_id}",
        "preset": "best-video",
        "playlist": False,
        "download_dir": "downloads",
        "filename_template": "%(title)s.%(ext)s",
        "added_at": "2026-04-24T00:00:00+00:00",
        "status": status,
    }


def _paths() -> AppPaths:
    root = Path(tempfile.mkdtemp())
    return AppPaths(
        data_dir=root / "data",
        settings_file=root / "data" / "settings.json",
        archive_file=root / "data" / "download-archive.txt",
        cookies_file=root / "data" / "cookies.txt",
        logs_dir=root / "data" / "logs",
        activity_log_file=root / "data" / "logs" / "activity.log",
        tools_dir=root / "data" / "tools",
        ytdlp_executable=root / "data" / "tools" / "yt-dlp.exe",
        ffmpeg_dir=root / "data" / "tools" / "ffmpeg",
        ffmpeg_executable=root / "data" / "tools" / "ffmpeg" / "ffmpeg.exe",
        ffprobe_executable=root / "data" / "tools" / "ffprobe.exe",
        deno_dir=root / "data" / "tools" / "deno",
        deno_executable=root / "data" / "tools" / "deno" / "deno.exe",
        download_dir=root / "downloads",
    )


if __name__ == "__main__":
    unittest.main()
