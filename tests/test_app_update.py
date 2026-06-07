from __future__ import annotations

import hashlib
import io
import json
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from ytdlp_helper import __version__
from ytdlp_helper.app_update import (
    APP_EXE_NAME,
    AppUpdateError,
    _extract_and_validate,
    _is_newer_version,
    _verify_sha256,
    _write_restart_script,
    check_and_stage_app_update,
    select_portable_assets,
)
from ytdlp_helper.update_service import UpdateService


class FakeResponse(io.BytesIO):
    def __enter__(self) -> "FakeResponse":
        return self

    def __exit__(self, *_args: object) -> None:
        return None


class AppUpdateTests(unittest.TestCase):
    def test_package_version_matches_pyproject(self) -> None:
        root = Path(__file__).resolve().parents[1]
        pyproject = root / "pyproject.toml"
        version_line = next(line for line in pyproject.read_text(encoding="utf-8").splitlines() if line.startswith("version = "))
        self.assertEqual(__version__, version_line.split('"')[1])

    def test_version_comparison_handles_v_tags(self) -> None:
        self.assertTrue(_is_newer_version("v0.1.1", "0.1.0"))
        self.assertFalse(_is_newer_version("v0.1.0", "0.1.0"))

    def test_select_portable_assets_requires_zip_and_matching_checksum(self) -> None:
        release = _release(
            "v0.1.1",
            [
                "YouTube-Download-Helper-v0.1.1-windows-portable.zip",
                "YouTube-Download-Helper-v0.1.1-windows-portable.zip.sha256",
            ],
        )

        zip_asset, checksum_asset = select_portable_assets(release)

        self.assertEqual(zip_asset.name, "YouTube-Download-Helper-v0.1.1-windows-portable.zip")
        self.assertEqual(checksum_asset.name, "YouTube-Download-Helper-v0.1.1-windows-portable.zip.sha256")

    def test_select_portable_assets_reports_missing_zip(self) -> None:
        with self.assertRaisesRegex(AppUpdateError, "missing the Windows portable ZIP"):
            select_portable_assets(_release("v0.1.1", ["readme.txt"]))

    def test_select_portable_assets_reports_missing_checksum(self) -> None:
        release = _release("v0.1.1", ["YouTube-Download-Helper-v0.1.1-windows-portable.zip"])

        with self.assertRaisesRegex(AppUpdateError, "missing the portable ZIP SHA256"):
            select_portable_assets(release)

    def test_checksum_mismatch_fails(self) -> None:
        root = Path(tempfile.mkdtemp())
        zip_path = root / "app.zip"
        checksum_path = root / "app.zip.sha256"
        zip_path.write_bytes(b"contents")
        checksum_path.write_text("0" * 64, encoding="utf-8")

        with self.assertRaisesRegex(AppUpdateError, "SHA256"):
            _verify_sha256(zip_path, checksum_path)

    def test_zip_validation_requires_app_executable(self) -> None:
        root = Path(tempfile.mkdtemp())
        zip_path = root / "app.zip"
        with zipfile.ZipFile(zip_path, "w") as archive:
            archive.writestr("not-the-app.txt", "nope")

        with self.assertRaisesRegex(AppUpdateError, APP_EXE_NAME):
            _extract_and_validate(zip_path, root / "stage")

    def test_source_mode_skips_app_replacement_after_release_check(self) -> None:
        release = _release(
            "v0.1.1",
            [
                "YouTube-Download-Helper-v0.1.1-windows-portable.zip",
                "YouTube-Download-Helper-v0.1.1-windows-portable.zip.sha256",
            ],
        )

        with (
            patch("ytdlp_helper.app_update.urllib.request.urlopen", return_value=FakeResponse(json.dumps(release).encode())),
            patch("ytdlp_helper.app_update.sys.frozen", False, create=True),
        ):
            result = check_and_stage_app_update(lambda *_args: None, lambda *_args: None, current_version="0.1.0")

        self.assertFalse(result.restart_ready)
        self.assertIn("only available in the packaged app", result.message)

    def test_frozen_mode_stages_update_and_generates_restart_script(self) -> None:
        root = Path(tempfile.mkdtemp())
        zip_bytes = _app_zip()
        checksum = hashlib.sha256(zip_bytes).hexdigest()
        release = _release(
            "v0.1.1",
            [
                "YouTube-Download-Helper-v0.1.1-windows-portable.zip",
                "YouTube-Download-Helper-v0.1.1-windows-portable.zip.sha256",
            ],
        )

        def fake_download(url: str, destination: Path, *_args: object) -> None:
            if url.endswith(".sha256"):
                destination.write_text(f"{checksum}  app.zip", encoding="utf-8")
            else:
                destination.write_bytes(zip_bytes)

        with (
            patch("ytdlp_helper.app_update.urllib.request.urlopen", return_value=FakeResponse(json.dumps(release).encode())),
            patch("ytdlp_helper.app_update._download_file", side_effect=fake_download),
            patch("ytdlp_helper.app_update.sys.frozen", True, create=True),
            patch("ytdlp_helper.app_update.sys.executable", str(root / APP_EXE_NAME)),
        ):
            result = check_and_stage_app_update(lambda *_args: None, lambda *_args: None, current_version="0.1.0")

        self.assertTrue(result.restart_ready)
        self.assertIsNotNone(result.restart_script)
        script = result.restart_script.read_text(encoding="utf-8") if result.restart_script else ""
        self.assertIn("Wait-Process", script)
        self.assertIn("backup", script)
        self.assertIn(APP_EXE_NAME, script)

    def test_restart_script_contains_restore_path(self) -> None:
        root = Path(tempfile.mkdtemp())
        script = _write_restart_script(root, 123, root / "app", root / "staged", APP_EXE_NAME)
        text = script.read_text(encoding="utf-8")

        self.assertIn("catch", text)
        self.assertIn("Move-Item", text)
        self.assertIn("Start-Process", text)

    def test_update_service_refreshes_runtime_tool_resolver_cache(self) -> None:
        paths = _paths()
        resolver = FakeRuntimeToolResolver()

        with patch("ytdlp_helper.update_service.check_and_stage_app_update", return_value=_update_result()):
            result = UpdateService(paths, resolver).update(lambda *_args: None, lambda *_args: None)

        self.assertEqual(result.message, "done")
        self.assertEqual(resolver.refresh_calls, 1)


def _release(tag_name: str, asset_names: list[str]) -> dict[str, object]:
    return {
        "tag_name": tag_name,
        "assets": [{"name": name, "browser_download_url": f"https://example.test/{name}"} for name in asset_names],
    }


def _app_zip() -> bytes:
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w") as archive:
        archive.writestr(APP_EXE_NAME, b"exe")
    return output.getvalue()


class FakeRuntimeToolResolver:
    def __init__(self) -> None:
        self.refresh_calls = 0

    def refresh(self, *_args: object) -> object:
        self.refresh_calls += 1
        return object()


def _update_result() -> object:
    from ytdlp_helper.app_update import AppUpdateResult

    return AppUpdateResult(message="done")


def _paths() -> object:
    from ytdlp_helper.config import AppPaths

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
        deno_dir=root / "data" / "tools" / "deno",
        deno_executable=root / "data" / "tools" / "deno" / "deno.exe",
        download_dir=root / "downloads",
    )


if __name__ == "__main__":
    unittest.main()
