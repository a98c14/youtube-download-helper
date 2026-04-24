from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from ytdlp_helper.app import YtDlpHelperApp


class FakeVar:
    def __init__(self, value: object | None = None) -> None:
        self.value = value

    def set(self, value: object) -> None:
        self.value = value


class AppStatusTests(unittest.TestCase):
    def test_installing_percent_updates_progress_bar(self) -> None:
        app = _app_with_status_vars()

        app._set_status("installing", "Downloading ffmpeg 62%")  # noqa: SLF001

        self.assertEqual(app.status_var.value, "Downloading ffmpeg 62%")
        self.assertEqual(app.progress_var.value, 62)

    def test_installing_without_percent_keeps_coarse_progress(self) -> None:
        app = _app_with_status_vars()

        app._set_status("installing", "Downloading ffmpeg 18.4 MB")  # noqa: SLF001

        self.assertEqual(app.status_var.value, "Downloading ffmpeg 18.4 MB")
        self.assertEqual(app.progress_var.value, 5)


def _app_with_status_vars() -> YtDlpHelperApp:
    app = YtDlpHelperApp.__new__(YtDlpHelperApp)
    app.status_var = FakeVar()
    app.speed_var = FakeVar()
    app.progress_var = FakeVar()
    return app


if __name__ == "__main__":
    unittest.main()
