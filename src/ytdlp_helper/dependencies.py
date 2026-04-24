from __future__ import annotations

import json
import shutil
import subprocess
import tempfile
import urllib.error
import urllib.request
import zipfile
from datetime import UTC, datetime
from pathlib import Path
from typing import Callable

from .config import AppPaths, find_ffmpeg_location, find_ytdlp_executable


LogCallback = Callable[[str], None]
StatusCallback = Callable[[str, str], None]

YTDLP_URL = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe"
FFMPEG_URL = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"
DOWNLOAD_CHUNK_BYTES = 1024 * 1024
UNKNOWN_TOTAL_LOG_BYTES = 5 * 1024 * 1024


class DependencyInstallError(RuntimeError):
    pass


def ensure_runtime_tools(
    paths: AppPaths,
    log_callback: LogCallback,
    status_callback: StatusCallback,
) -> None:
    ensure_ytdlp(paths, log_callback, status_callback)
    ensure_ffmpeg(paths, log_callback, status_callback)


def ensure_ytdlp(
    paths: AppPaths,
    log_callback: LogCallback,
    status_callback: StatusCallback,
) -> None:
    if find_ytdlp_executable(paths):
        return

    status_callback("installing", "Installing yt-dlp")
    log_callback(f"yt-dlp not found; downloading {YTDLP_URL}")
    paths.tools_dir.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory(prefix="ytdlp-helper-") as temp_dir:
        temp_path = Path(temp_dir)
        download_path = temp_path / "yt-dlp.exe"
        try:
            _download_file(YTDLP_URL, download_path, "yt-dlp", log_callback, status_callback)
            if not download_path.exists() or download_path.stat().st_size == 0:
                raise DependencyInstallError("Downloaded yt-dlp.exe was empty.")
            shutil.copy2(download_path, paths.ytdlp_executable)
            version = _read_tool_version(paths.ytdlp_executable)
            _write_metadata(paths, "yt-dlp", YTDLP_URL, paths.ytdlp_executable, version)
        except (OSError, urllib.error.URLError, DependencyInstallError) as exc:
            _remove_file(paths.ytdlp_executable)
            log_callback(f"yt-dlp install failed from {YTDLP_URL}: {exc}")
            raise DependencyInstallError(
                "Could not install yt-dlp. Check your internet connection and click Download again."
            ) from exc

    log_callback(f"Installed yt-dlp to {paths.ytdlp_executable}")


def ensure_ffmpeg(
    paths: AppPaths,
    log_callback: LogCallback,
    status_callback: StatusCallback,
) -> None:
    if find_ffmpeg_location(paths):
        return

    status_callback("installing", "Installing ffmpeg")
    log_callback(f"ffmpeg not found; downloading {FFMPEG_URL}")
    paths.tools_dir.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory(prefix="ytdlp-helper-") as temp_dir:
        temp_path = Path(temp_dir)
        archive_path = temp_path / "ffmpeg.zip"
        extract_dir = temp_path / "extract"
        stage_dir = temp_path / "ffmpeg"
        try:
            _download_file(FFMPEG_URL, archive_path, "ffmpeg", log_callback, status_callback)
            with zipfile.ZipFile(archive_path) as archive:
                archive.extractall(extract_dir)
            ffmpeg_exe = _find_extracted_executable(extract_dir, "ffmpeg.exe")
            ffprobe_exe = _find_extracted_executable(extract_dir, "ffprobe.exe")
            if not ffmpeg_exe or not ffprobe_exe:
                raise DependencyInstallError("The FFmpeg ZIP did not contain ffmpeg.exe and ffprobe.exe.")

            stage_dir.mkdir(parents=True)
            shutil.copy2(ffmpeg_exe, stage_dir / "ffmpeg.exe")
            shutil.copy2(ffprobe_exe, stage_dir / "ffprobe.exe")
            if not (stage_dir / "ffmpeg.exe").exists() or not (stage_dir / "ffprobe.exe").exists():
                raise DependencyInstallError("Staged FFmpeg install is incomplete.")

            if paths.ffmpeg_dir.exists():
                shutil.rmtree(paths.ffmpeg_dir)
            shutil.move(str(stage_dir), str(paths.ffmpeg_dir))
            version = _read_tool_version(paths.ffmpeg_executable)
            _write_metadata(paths, "ffmpeg", FFMPEG_URL, paths.ffmpeg_dir, version)
        except (OSError, urllib.error.URLError, zipfile.BadZipFile, DependencyInstallError) as exc:
            if paths.ffmpeg_dir.exists() and (
                not paths.ffmpeg_executable.exists() or not paths.ffprobe_executable.exists()
            ):
                shutil.rmtree(paths.ffmpeg_dir, ignore_errors=True)
            log_callback(f"ffmpeg install failed from {FFMPEG_URL}: {exc}")
            raise DependencyInstallError(
                "Could not install ffmpeg. Check your internet connection and click Download again."
            ) from exc

    log_callback(f"Installed ffmpeg to {paths.ffmpeg_dir}")


def _download_file(
    url: str,
    destination: Path,
    tool_name: str,
    log_callback: LogCallback,
    status_callback: StatusCallback,
) -> None:
    log_callback(f"Downloading {url}")
    request = urllib.request.Request(url, headers={"User-Agent": "YT-DLP Helper"})
    with urllib.request.urlopen(request, timeout=60) as response:
        total_bytes = _content_length(response)
        bytes_downloaded = 0
        last_percent = -1
        next_unknown_total_log = UNKNOWN_TOTAL_LOG_BYTES
        with destination.open("wb") as output:
            while True:
                chunk = response.read(DOWNLOAD_CHUNK_BYTES)
                if not chunk:
                    break
                output.write(chunk)
                bytes_downloaded += len(chunk)

                if total_bytes:
                    percent = min(100, int(bytes_downloaded * 100 / total_bytes))
                    if percent != last_percent:
                        message = f"Downloading {tool_name} {percent}%"
                        status_callback("installing", message)
                        if percent == 100 or percent // 10 > last_percent // 10:
                            log_callback(message)
                        last_percent = percent
                elif bytes_downloaded >= next_unknown_total_log:
                    message = f"Downloading {tool_name} {_format_megabytes(bytes_downloaded)} MB"
                    status_callback("installing", message)
                    log_callback(message)
                    next_unknown_total_log += UNKNOWN_TOTAL_LOG_BYTES
    log_callback(f"Downloaded to temporary file {destination}")


def _content_length(response: object) -> int | None:
    headers = getattr(response, "headers", None)
    if not headers:
        return None
    raw_value = headers.get("Content-Length")
    if not raw_value:
        return None
    try:
        total_bytes = int(raw_value)
    except ValueError:
        return None
    return total_bytes if total_bytes > 0 else None


def _format_megabytes(byte_count: int) -> str:
    return f"{byte_count / (1024 * 1024):.1f}"


def _find_extracted_executable(root: Path, name: str) -> Path | None:
    for candidate in root.rglob(name):
        if candidate.is_file():
            return candidate
    return None


def _write_metadata(paths: AppPaths, tool: str, source_url: str, install_path: Path, version: str | None) -> None:
    metadata = {
        "tool": tool,
        "source_url": source_url,
        "install_path": str(install_path),
        "installed_at": datetime.now(UTC).isoformat(),
        "version": version,
    }
    metadata_path = paths.tools_dir / f"{tool}.json"
    metadata_path.write_text(json.dumps(metadata, indent=2), encoding="utf-8")


def _read_tool_version(executable: Path) -> str | None:
    try:
        result = subprocess.run(
            [str(executable), "-version" if executable.name == "ffmpeg.exe" else "--version"],
            capture_output=True,
            text=True,
            check=False,
            timeout=10,
        )
    except (OSError, subprocess.TimeoutExpired):
        return None
    if result.returncode != 0:
        return None
    first_line = (result.stdout or result.stderr).splitlines()
    return first_line[0].strip() if first_line else None


def _remove_file(path: Path) -> None:
    try:
        path.unlink()
    except FileNotFoundError:
        return
