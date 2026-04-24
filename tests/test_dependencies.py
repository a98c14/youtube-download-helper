from __future__ import annotations

import io
import json
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from ytdlp_helper.config import AppPaths, find_ffmpeg_location, find_ytdlp_executable
from ytdlp_helper.dependencies import (
    DependencyInstallError,
    _download_file,
    ensure_ffmpeg,
    ensure_ytdlp,
    refresh_runtime_tools,
)


class FakeResponse(io.BytesIO):
    def __init__(self, payload: bytes, content_length: int | None = None) -> None:
        super().__init__(payload)
        self.headers: dict[str, str] = {}
        if content_length is not None:
            self.headers["Content-Length"] = str(content_length)


class FakeUrlOpen:
    def __init__(self, payload: bytes, content_length: int | None = None) -> None:
        self._response = FakeResponse(payload, content_length)

    def __enter__(self) -> FakeResponse:
        return self._response

    def __exit__(self, *_args: object) -> None:
        return None


class DependencyTests(unittest.TestCase):
    def test_discovery_prefers_managed_tools(self) -> None:
        paths = _paths()
        paths.ytdlp_executable.parent.mkdir(parents=True)
        paths.ytdlp_executable.write_bytes(b"exe")
        paths.ffmpeg_dir.mkdir(parents=True)
        paths.ffmpeg_executable.write_bytes(b"exe")
        paths.ffprobe_executable.write_bytes(b"exe")

        with (
            patch("ytdlp_helper.config.shutil.which", return_value="C:/path/yt-dlp.exe"),
            patch("ytdlp_helper.config.sys.frozen", False, create=True),
        ):
            self.assertEqual(find_ytdlp_executable(paths), str(paths.ytdlp_executable))
        self.assertEqual(find_ffmpeg_location(paths), str(paths.ffmpeg_dir))

    def test_missing_ytdlp_downloads_and_installs_exe(self) -> None:
        paths = _paths()
        logs: list[str] = []
        statuses: list[tuple[str, str]] = []

        with (
            patch("ytdlp_helper.dependencies.find_ytdlp_executable", return_value=None),
            patch("ytdlp_helper.dependencies.urllib.request.urlopen", return_value=FakeUrlOpen(b"yt-dlp")),
            patch("ytdlp_helper.dependencies._read_tool_version", return_value="2026.04.01"),
        ):
            ensure_ytdlp(paths, logs.append, lambda status, message: statuses.append((status, message)))

        self.assertEqual(paths.ytdlp_executable.read_bytes(), b"yt-dlp")
        self.assertIn(("installing", "Installing yt-dlp"), statuses)
        metadata = json.loads((paths.tools_dir / "yt-dlp.json").read_text(encoding="utf-8"))
        self.assertEqual(metadata["tool"], "yt-dlp")
        self.assertEqual(metadata["version"], "2026.04.01")
        self.assertTrue(any("github.com/yt-dlp" in line for line in logs))

    def test_missing_ffmpeg_extracts_zip_and_installs_both_executables(self) -> None:
        paths = _paths()
        payload = _ffmpeg_zip({"ffmpeg-essentials/bin/ffmpeg.exe": b"ffmpeg", "ffmpeg-essentials/bin/ffprobe.exe": b"ffprobe"})
        logs: list[str] = []

        with (
            patch("ytdlp_helper.dependencies.find_ffmpeg_location", return_value=None),
            patch("ytdlp_helper.dependencies.urllib.request.urlopen", return_value=FakeUrlOpen(payload)),
            patch("ytdlp_helper.dependencies._read_tool_version", return_value="ffmpeg version 7"),
        ):
            ensure_ffmpeg(paths, logs.append, lambda *_args: None)

        self.assertEqual(paths.ffmpeg_executable.read_bytes(), b"ffmpeg")
        self.assertEqual(paths.ffprobe_executable.read_bytes(), b"ffprobe")
        metadata = json.loads((paths.tools_dir / "ffmpeg.json").read_text(encoding="utf-8"))
        self.assertEqual(metadata["tool"], "ffmpeg")
        self.assertEqual(metadata["install_path"], str(paths.ffmpeg_dir))
        self.assertTrue(any("gyan.dev" in line for line in logs))

    def test_refresh_runtime_tools_replaces_existing_managed_tools(self) -> None:
        paths = _paths()
        paths.ytdlp_executable.parent.mkdir(parents=True)
        paths.ytdlp_executable.write_bytes(b"old yt-dlp")
        paths.ffmpeg_dir.mkdir(parents=True)
        paths.ffmpeg_executable.write_bytes(b"old ffmpeg")
        paths.ffprobe_executable.write_bytes(b"old ffprobe")
        payloads = [
            FakeUrlOpen(b"new yt-dlp"),
            FakeUrlOpen(
                _ffmpeg_zip(
                    {
                        "ffmpeg-essentials/bin/ffmpeg.exe": b"new ffmpeg",
                        "ffmpeg-essentials/bin/ffprobe.exe": b"new ffprobe",
                    }
                )
            ),
        ]
        statuses: list[tuple[str, str]] = []

        with (
            patch("ytdlp_helper.dependencies.urllib.request.urlopen", side_effect=payloads),
            patch("ytdlp_helper.dependencies._fetch_latest_ytdlp_version", return_value="latest"),
            patch("ytdlp_helper.dependencies._fetch_latest_ffmpeg_version", return_value="new"),
            patch("ytdlp_helper.dependencies._read_source_identity", return_value={"etag": "new-ffmpeg"}),
            patch("ytdlp_helper.dependencies._read_tool_version", side_effect=["old", "latest", "ffmpeg version old", "ffmpeg version new"]),
        ):
            refresh_runtime_tools(paths, lambda *_args: None, lambda status, message: statuses.append((status, message)))

        self.assertEqual(paths.ytdlp_executable.read_bytes(), b"new yt-dlp")
        self.assertEqual(paths.ffmpeg_executable.read_bytes(), b"new ffmpeg")
        self.assertEqual(paths.ffprobe_executable.read_bytes(), b"new ffprobe")
        self.assertIn(("installing", "Updating runtime tools"), statuses)

    def test_refresh_runtime_tools_skips_current_managed_tools(self) -> None:
        paths = _paths()
        paths.ytdlp_executable.parent.mkdir(parents=True)
        paths.ytdlp_executable.write_bytes(b"current yt-dlp")
        paths.ffmpeg_dir.mkdir(parents=True)
        paths.ffmpeg_executable.write_bytes(b"current ffmpeg")
        paths.ffprobe_executable.write_bytes(b"current ffprobe")
        paths.tools_dir.mkdir(parents=True, exist_ok=True)
        (paths.tools_dir / "ffmpeg.json").write_text(
            json.dumps({"version": "ffmpeg version current", "source_identity": {"etag": "current-ffmpeg"}}),
            encoding="utf-8",
        )
        logs: list[str] = []

        with (
            patch("ytdlp_helper.dependencies._fetch_latest_ytdlp_version", return_value="2026.04.01"),
            patch("ytdlp_helper.dependencies._fetch_latest_ffmpeg_version", return_value="current"),
            patch("ytdlp_helper.dependencies._read_source_identity", side_effect=AssertionError("checked ffmpeg package")),
            patch("ytdlp_helper.dependencies._read_tool_version", side_effect=["2026.04.01", "ffmpeg version current"]),
            patch("ytdlp_helper.dependencies.urllib.request.urlopen", side_effect=AssertionError("downloaded")),
        ):
            refresh_runtime_tools(paths, logs.append, lambda *_args: None)

        self.assertEqual(paths.ytdlp_executable.read_bytes(), b"current yt-dlp")
        self.assertEqual(paths.ffmpeg_executable.read_bytes(), b"current ffmpeg")
        self.assertEqual(paths.ffprobe_executable.read_bytes(), b"current ffprobe")
        self.assertTrue(any("yt-dlp is already current" in line for line in logs))
        self.assertTrue(any("ffmpeg is already current" in line for line in logs))

    def test_partial_ffmpeg_install_is_not_promoted(self) -> None:
        paths = _paths()
        payload = _ffmpeg_zip({"ffmpeg-essentials/bin/ffmpeg.exe": b"ffmpeg"})
        logs: list[str] = []

        with (
            patch("ytdlp_helper.dependencies.find_ffmpeg_location", return_value=None),
            patch("ytdlp_helper.dependencies.urllib.request.urlopen", return_value=FakeUrlOpen(payload)),
        ):
            with self.assertRaisesRegex(DependencyInstallError, "Could not install ffmpeg"):
                ensure_ffmpeg(paths, logs.append, lambda *_args: None)

        self.assertFalse(paths.ffmpeg_dir.exists())
        self.assertTrue(any("did not contain ffmpeg.exe and ffprobe.exe" in line for line in logs))

    def test_install_failure_logs_details_and_raises_actionable_error(self) -> None:
        paths = _paths()
        logs: list[str] = []

        with (
            patch("ytdlp_helper.dependencies.find_ytdlp_executable", return_value=None),
            patch("ytdlp_helper.dependencies.urllib.request.urlopen", side_effect=OSError("network down")),
        ):
            with self.assertRaisesRegex(DependencyInstallError, "click Download again"):
                ensure_ytdlp(paths, logs.append, lambda *_args: None)

        self.assertFalse(paths.ytdlp_executable.exists())
        self.assertTrue(any("network down" in line for line in logs))

    def test_refresh_ytdlp_failure_keeps_existing_managed_executable(self) -> None:
        paths = _paths()
        paths.ytdlp_executable.parent.mkdir(parents=True)
        paths.ytdlp_executable.write_bytes(b"old yt-dlp")

        with patch("ytdlp_helper.dependencies.urllib.request.urlopen", side_effect=OSError("network down")):
            with self.assertRaisesRegex(DependencyInstallError, "Could not check the latest yt-dlp"):
                refresh_runtime_tools(paths, lambda *_args: None, lambda *_args: None)

        self.assertEqual(paths.ytdlp_executable.read_bytes(), b"old yt-dlp")

    def test_download_file_reports_percent_progress_when_content_length_exists(self) -> None:
        root = Path(tempfile.mkdtemp())
        destination = root / "tool.exe"
        payload = b"x" * (3 * 1024 * 1024)
        logs: list[str] = []
        statuses: list[tuple[str, str]] = []

        with patch(
            "ytdlp_helper.dependencies.urllib.request.urlopen",
            return_value=FakeUrlOpen(payload, content_length=len(payload)),
        ):
            _download_file(
                "https://example.test/tool.exe",
                destination,
                "yt-dlp",
                logs.append,
                lambda status, message: statuses.append((status, message)),
            )

        self.assertEqual(destination.read_bytes(), payload)
        self.assertIn(("installing", "Downloading yt-dlp 33%"), statuses)
        self.assertIn(("installing", "Downloading yt-dlp 66%"), statuses)
        self.assertIn(("installing", "Downloading yt-dlp 100%"), statuses)
        self.assertIn("Downloading yt-dlp 100%", logs)

    def test_download_file_reports_megabyte_progress_without_content_length(self) -> None:
        root = Path(tempfile.mkdtemp())
        destination = root / "ffmpeg.zip"
        payload = b"x" * (6 * 1024 * 1024)
        logs: list[str] = []
        statuses: list[tuple[str, str]] = []

        with patch("ytdlp_helper.dependencies.urllib.request.urlopen", return_value=FakeUrlOpen(payload)):
            _download_file(
                "https://example.test/ffmpeg.zip",
                destination,
                "ffmpeg",
                logs.append,
                lambda status, message: statuses.append((status, message)),
            )

        self.assertEqual(destination.read_bytes(), payload)
        self.assertIn(("installing", "Downloading ffmpeg 5.0 MB"), statuses)
        self.assertIn("Downloading ffmpeg 5.0 MB", logs)


def _ffmpeg_zip(files: dict[str, bytes]) -> bytes:
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w") as archive:
        for name, contents in files.items():
            archive.writestr(name, contents)
    return output.getvalue()


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
        ffprobe_executable=root / "data" / "tools" / "ffmpeg" / "ffprobe.exe",
        download_dir=root / "downloads",
    )


if __name__ == "__main__":
    unittest.main()
