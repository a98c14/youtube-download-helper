from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import tempfile
import threading
import urllib.error
import urllib.request
import zipfile
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import Callable

from .config import AppPaths, find_deno_executable, find_ffmpeg_location, find_ytdlp_executable


from .worker_status import (
    AppUpdatePhase,
    AppUpdateStatus,
    RuntimeTool,
    RuntimeToolPhase,
    RuntimeToolStatus,
    StatusEvent,
    WorkerPhase,
)


LogCallback = Callable[[str], None]
StatusCallback = Callable[[WorkerPhase, StatusEvent], None]

YTDLP_URL = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe"
FFMPEG_URL = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"
YTDLP_RELEASE_API_URL = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest"
FFMPEG_RELEASE_VERSION_URL = "https://www.gyan.dev/ffmpeg/builds/release-version"
DENO_RELEASE_API_URL = "https://api.github.com/repos/denoland/deno/releases/latest"
DENO_WINDOWS_ASSET_NAME = "deno-x86_64-pc-windows-msvc.zip"
DOWNLOAD_CHUNK_BYTES = 1024 * 1024
UNKNOWN_TOTAL_LOG_BYTES = 5 * 1024 * 1024


class DependencyInstallError(RuntimeError):
    pass


@dataclass(frozen=True)
class RuntimeToolContext:
    ytdlp_executable: str
    ffmpeg_location: str | None
    deno_executable: str | None
    ytdlp_version: str | None = None
    ffmpeg_version: str | None = None
    deno_version: str | None = None


class RuntimeToolResolver:
    def __init__(self, paths: AppPaths) -> None:
        self._paths = paths
        self._context: RuntimeToolContext | None = None
        self._resolving = False
        self._condition = threading.Condition(threading.RLock())

    def resolve(self, log_callback: LogCallback, status_callback: StatusCallback) -> RuntimeToolContext:
        context = self._cached_context()
        if context:
            return context
        return self._resolve_single_flight(log_callback, status_callback, refresh=False)

    def refresh(self, log_callback: LogCallback, status_callback: StatusCallback) -> RuntimeToolContext:
        return self._resolve_single_flight(log_callback, status_callback, refresh=True)

    def _cached_context(self) -> RuntimeToolContext | None:
        with self._condition:
            context = self._context
            if context and _context_paths_exist(context):
                return context
            if context:
                self._context = None
            return None

    def _resolve_single_flight(
        self,
        log_callback: LogCallback,
        status_callback: StatusCallback,
        *,
        refresh: bool,
    ) -> RuntimeToolContext:
        with self._condition:
            while self._resolving:
                self._condition.wait()
                if not refresh and self._context and _context_paths_exist(self._context):
                    return self._context
            if not refresh and self._context and _context_paths_exist(self._context):
                return self._context
            self._resolving = True

        try:
            context = self._resolve_fresh(log_callback, status_callback, refresh=refresh)
        except Exception:
            with self._condition:
                self._resolving = False
                if refresh:
                    self._context = None
                self._condition.notify_all()
            raise

        with self._condition:
            self._context = context
            self._resolving = False
            self._condition.notify_all()
        return context

    def _resolve_fresh(
        self,
        log_callback: LogCallback,
        status_callback: StatusCallback,
        *,
        refresh: bool,
    ) -> RuntimeToolContext:
        if refresh:
            refresh_runtime_tools(self._paths, log_callback, status_callback)
        else:
            ensure_runtime_tools(self._paths, log_callback, status_callback)
        context = resolve_runtime_tool_context(self._paths)
        log_runtime_tool_context(context, log_callback)
        return context


def ensure_runtime_tools(
    paths: AppPaths,
    log_callback: LogCallback,
    status_callback: StatusCallback,
) -> None:
    ensure_ytdlp(paths, log_callback, status_callback)
    ensure_ffmpeg(paths, log_callback, status_callback)
    ensure_deno(paths, log_callback, status_callback)


def refresh_runtime_tools(
    paths: AppPaths,
    log_callback: LogCallback,
    status_callback: StatusCallback,
) -> None:
    status_callback("installing", RuntimeToolStatus(RuntimeTool.YTDLP, RuntimeToolPhase.UPDATING))
    log_callback("Checking app-managed runtime tools")
    refresh_ytdlp(paths, log_callback, status_callback)
    refresh_ffmpeg(paths, log_callback, status_callback)
    refresh_deno(paths, log_callback, status_callback)


def refresh_ytdlp(
    paths: AppPaths,
    log_callback: LogCallback,
    status_callback: StatusCallback,
) -> None:
    if not paths.ytdlp_executable.exists():
        install_ytdlp(paths, log_callback, status_callback)
        return

    status_callback("installing", RuntimeToolStatus(RuntimeTool.YTDLP, RuntimeToolPhase.CHECKING))
    latest_version = _fetch_latest_ytdlp_version()
    installed_version = _read_tool_version(paths.ytdlp_executable)
    if installed_version and _normalize_version(installed_version) == _normalize_version(latest_version):
        log_callback(f"yt-dlp is already current at {installed_version}.")
        return

    log_callback(f"yt-dlp update available: {installed_version or 'unknown'} -> {latest_version}")
    install_ytdlp(paths, log_callback, status_callback)


def refresh_ffmpeg(
    paths: AppPaths,
    log_callback: LogCallback,
    status_callback: StatusCallback,
) -> None:
    if not paths.ffmpeg_executable.exists() or not paths.ffprobe_executable.exists():
        install_ffmpeg(paths, log_callback, status_callback)
        return

    status_callback("installing", RuntimeToolStatus(RuntimeTool.FFMPEG, RuntimeToolPhase.CHECKING))
    latest_version = _fetch_latest_ffmpeg_version()
    installed_version = _read_tool_version(paths.ffmpeg_executable)
    if installed_version and _ffmpeg_version_matches(installed_version, latest_version):
        log_callback(f"ffmpeg is already current at {installed_version}.")
        return

    latest_source = _read_source_identity(FFMPEG_URL)
    if latest_version:
        latest_source["release-version"] = latest_version
    metadata = _read_metadata(paths, "ffmpeg")
    if latest_source and metadata and _metadata_matches_source(metadata, latest_source):
        version = metadata.get("version") or _read_tool_version(paths.ffmpeg_executable) or "installed version"
        log_callback(f"ffmpeg is already current at {version}.")
        return

    log_callback(f"ffmpeg update available: {installed_version or 'unknown'} -> {latest_version}")
    install_ffmpeg(paths, log_callback, status_callback, source_identity=latest_source)


def refresh_deno(
    paths: AppPaths,
    log_callback: LogCallback,
    status_callback: StatusCallback,
) -> None:
    if not paths.deno_executable.exists():
        install_deno(paths, log_callback, status_callback)
        return

    status_callback("installing", RuntimeToolStatus(RuntimeTool.DENO, RuntimeToolPhase.CHECKING))
    latest_version, asset_url = _fetch_latest_deno_release()
    installed_version = _read_tool_version(paths.deno_executable)
    if installed_version and _deno_version_matches(installed_version, latest_version):
        log_callback(f"Deno is already current at {installed_version}.")
        return

    log_callback(f"Deno update available: {installed_version or 'unknown'} -> {latest_version}")
    install_deno(paths, log_callback, status_callback, asset_url=asset_url)


def ensure_ytdlp(
    paths: AppPaths,
    log_callback: LogCallback,
    status_callback: StatusCallback,
) -> None:
    if find_ytdlp_executable(paths):
        return

    install_ytdlp(paths, log_callback, status_callback)


def install_ytdlp(
    paths: AppPaths,
    log_callback: LogCallback,
    status_callback: StatusCallback,
) -> None:

    status_callback("installing", RuntimeToolStatus(RuntimeTool.YTDLP, RuntimeToolPhase.INSTALLING))
    log_callback(f"Downloading fresh yt-dlp from {YTDLP_URL}")
    paths.tools_dir.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory(prefix="ytdlp-helper-") as temp_dir:
        temp_path = Path(temp_dir)
        download_path = temp_path / "yt-dlp.exe"
        try:
            _download_file(YTDLP_URL, download_path, RuntimeTool.YTDLP, log_callback, status_callback)
            if not download_path.exists() or download_path.stat().st_size == 0:
                raise DependencyInstallError("Downloaded yt-dlp.exe was empty.")
            os.replace(download_path, paths.ytdlp_executable)
            version = _read_tool_version(paths.ytdlp_executable)
            _write_metadata(paths, "yt-dlp", YTDLP_URL, paths.ytdlp_executable, version, {})
        except (OSError, urllib.error.URLError, DependencyInstallError) as exc:
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

    install_ffmpeg(paths, log_callback, status_callback)


def ensure_deno(
    paths: AppPaths,
    log_callback: LogCallback,
    status_callback: StatusCallback,
) -> None:
    if find_deno_executable(paths):
        return

    install_deno(paths, log_callback, status_callback)


def install_ffmpeg(
    paths: AppPaths,
    log_callback: LogCallback,
    status_callback: StatusCallback,
    source_identity: dict[str, str] | None = None,
) -> None:

    status_callback("installing", RuntimeToolStatus(RuntimeTool.FFMPEG, RuntimeToolPhase.INSTALLING))
    log_callback(f"Downloading fresh ffmpeg from {FFMPEG_URL}")
    paths.tools_dir.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory(prefix="ytdlp-helper-") as temp_dir:
        temp_path = Path(temp_dir)
        archive_path = temp_path / "ffmpeg.zip"
        extract_dir = temp_path / "extract"
        stage_dir = temp_path / "ffmpeg"
        try:
            _download_file(FFMPEG_URL, archive_path, RuntimeTool.FFMPEG, log_callback, status_callback)
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
            _write_metadata(paths, "ffmpeg", FFMPEG_URL, paths.ffmpeg_dir, version, source_identity or {})
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


def install_deno(
    paths: AppPaths,
    log_callback: LogCallback,
    status_callback: StatusCallback,
    asset_url: str | None = None,
) -> None:

    status_callback("installing", RuntimeToolStatus(RuntimeTool.DENO, RuntimeToolPhase.INSTALLING))
    version: str | None = None
    if asset_url is None:
        version, asset_url = _fetch_latest_deno_release()
    log_callback(f"Downloading fresh Deno from {asset_url}")
    paths.tools_dir.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory(prefix="ytdlp-helper-") as temp_dir:
        temp_path = Path(temp_dir)
        archive_path = temp_path / "deno.zip"
        extract_dir = temp_path / "extract"
        stage_dir = temp_path / "deno"
        try:
            _download_file(asset_url, archive_path, RuntimeTool.DENO, log_callback, status_callback)
            with zipfile.ZipFile(archive_path) as archive:
                archive.extractall(extract_dir)
            deno_exe = _find_extracted_executable(extract_dir, "deno.exe")
            if not deno_exe:
                raise DependencyInstallError("The Deno ZIP did not contain deno.exe.")

            stage_dir.mkdir(parents=True)
            shutil.copy2(deno_exe, stage_dir / "deno.exe")
            if not (stage_dir / "deno.exe").exists():
                raise DependencyInstallError("Staged Deno install is incomplete.")

            if paths.deno_dir.exists():
                shutil.rmtree(paths.deno_dir)
            shutil.move(str(stage_dir), str(paths.deno_dir))
            installed_version = _read_tool_version(paths.deno_executable)
            _write_metadata(paths, "deno", asset_url, paths.deno_dir, installed_version or version, {})
        except (OSError, urllib.error.URLError, zipfile.BadZipFile, DependencyInstallError) as exc:
            if paths.deno_dir.exists() and not paths.deno_executable.exists():
                shutil.rmtree(paths.deno_dir, ignore_errors=True)
            log_callback(f"Deno install failed from {asset_url}: {exc}")
            raise DependencyInstallError(
                "Could not install Deno. Check your internet connection and click Download again."
            ) from exc

    log_callback(f"Installed Deno to {paths.deno_dir}")


def _download_file(
    url: str,
    destination: Path,
    tool: RuntimeTool | None,
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
                        _emit_download_progress(tool, percent, None, log_callback, status_callback)
                        last_percent = percent
                elif bytes_downloaded >= next_unknown_total_log:
                    size_mb = _format_megabytes(bytes_downloaded)
                    _emit_download_progress(tool, None, size_mb, log_callback, status_callback)
                    next_unknown_total_log += UNKNOWN_TOTAL_LOG_BYTES
    log_callback(f"Downloaded to temporary file {destination}")


def _emit_download_progress(
    tool: RuntimeTool | None,
    percent: int | None,
    size_mb: str | None,
    log_callback: LogCallback,
    status_callback: StatusCallback,
) -> None:
    if tool is not None:
        event: StatusEvent = RuntimeToolStatus(tool, RuntimeToolPhase.DOWNLOADING, percent=percent, size_mb=size_mb)
    else:
        event = AppUpdateStatus(AppUpdatePhase.DOWNLOADING, percent=percent)
    status_callback("installing", event)
    if tool is not None and percent is not None:
        desc = f"Downloading {tool.display_name} {percent}%"
    elif tool is not None and size_mb is not None:
        desc = f"Downloading {tool.display_name} {size_mb} MB"
    elif percent is not None:
        desc = f"Downloading {percent}%"
    else:
        return
    log_callback(desc)


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


def _write_metadata(
    paths: AppPaths,
    tool: str,
    source_url: str,
    install_path: Path,
    version: str | None,
    source_identity: dict[str, str],
) -> None:
    metadata = {
        "tool": tool,
        "source_url": source_url,
        "install_path": str(install_path),
        "installed_at": datetime.now(UTC).isoformat(),
        "version": version,
        "source_identity": source_identity,
    }
    metadata_path = paths.tools_dir / f"{tool}.json"
    metadata_path.write_text(json.dumps(metadata, indent=2), encoding="utf-8")


def _read_metadata(paths: AppPaths, tool: str) -> dict[str, object] | None:
    metadata_path = paths.tools_dir / f"{tool}.json"
    try:
        data = json.loads(metadata_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None
    return data if isinstance(data, dict) else None


def _fetch_latest_ytdlp_version() -> str:
    request = urllib.request.Request(YTDLP_RELEASE_API_URL, headers={"User-Agent": "YT-DLP Helper"})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            payload = response.read()
    except (OSError, urllib.error.URLError) as exc:
        raise DependencyInstallError("Could not check the latest yt-dlp version. Check your internet connection.") from exc

    try:
        data = json.loads(payload.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise DependencyInstallError("GitHub returned an invalid yt-dlp release response.") from exc
    if not isinstance(data, dict):
        raise DependencyInstallError("GitHub returned an invalid yt-dlp release response.")
    tag_name = str(data.get("tag_name", "")).strip()
    if not tag_name:
        raise DependencyInstallError("GitHub did not return a yt-dlp release version.")
    return tag_name


def _fetch_latest_ffmpeg_version() -> str:
    request = urllib.request.Request(FFMPEG_RELEASE_VERSION_URL, headers={"User-Agent": "YT-DLP Helper"})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            payload = response.read()
    except (OSError, urllib.error.URLError) as exc:
        raise DependencyInstallError("Could not check the latest ffmpeg version. Check your internet connection.") from exc

    try:
        version = payload.decode("utf-8").strip()
    except UnicodeDecodeError as exc:
        raise DependencyInstallError("Gyan.dev returned an invalid ffmpeg version response.") from exc
    if not version:
        raise DependencyInstallError("Gyan.dev did not return a ffmpeg release version.")
    return version


def _fetch_latest_deno_release() -> tuple[str, str]:
    request = urllib.request.Request(DENO_RELEASE_API_URL, headers={"User-Agent": "YT-DLP Helper"})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            payload = response.read()
    except (OSError, urllib.error.URLError) as exc:
        raise DependencyInstallError("Could not check the latest Deno version. Check your internet connection.") from exc

    try:
        data = json.loads(payload.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise DependencyInstallError("GitHub returned an invalid Deno release response.") from exc
    if not isinstance(data, dict):
        raise DependencyInstallError("GitHub returned an invalid Deno release response.")

    tag_name = str(data.get("tag_name", "")).strip()
    assets = data.get("assets")
    if not tag_name or not isinstance(assets, list):
        raise DependencyInstallError("GitHub did not return a usable Deno release.")

    for asset in assets:
        if not isinstance(asset, dict):
            continue
        if asset.get("name") == DENO_WINDOWS_ASSET_NAME:
            download_url = str(asset.get("browser_download_url", "")).strip()
            if download_url:
                return tag_name, download_url

    raise DependencyInstallError("GitHub did not return the Windows Deno package.")


def _read_source_identity(url: str) -> dict[str, str]:
    request = urllib.request.Request(url, method="HEAD", headers={"User-Agent": "YT-DLP Helper"})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            headers = getattr(response, "headers", None)
    except (OSError, urllib.error.URLError) as exc:
        raise DependencyInstallError("Could not check the latest ffmpeg package. Check your internet connection.") from exc

    identity: dict[str, str] = {}
    if headers:
        for header in ("ETag", "Last-Modified", "Content-Length"):
            value = headers.get(header)
            if value:
                identity[header.lower()] = str(value)
    return identity


def _metadata_matches_source(metadata: dict[str, object], source_identity: dict[str, str]) -> bool:
    stored_identity = metadata.get("source_identity")
    if not isinstance(stored_identity, dict) or not source_identity:
        return False
    return all(stored_identity.get(key) == value for key, value in source_identity.items())


def _normalize_version(version: str) -> str:
    normalized = version.strip()
    if normalized.startswith("v"):
        normalized = normalized[1:]
    return normalized


def _ffmpeg_version_matches(installed_version: str, latest_version: str) -> bool:
    return re.search(rf"\b{re.escape(latest_version)}\b", installed_version) is not None


def _deno_version_matches(installed_version: str, latest_version: str) -> bool:
    return _normalize_version(latest_version) in _normalize_version(installed_version)


def read_tool_version(executable: Path) -> str | None:
    return _read_tool_version(executable)


def resolve_runtime_tool_context(paths: AppPaths) -> RuntimeToolContext:
    ytdlp_executable = find_ytdlp_executable(paths)
    if not ytdlp_executable:
        raise RuntimeError(
            "yt-dlp.exe was not found and could not be installed. Check your internet connection and try again."
        )
    ffmpeg_location = find_ffmpeg_location(paths)
    deno_executable = find_deno_executable(paths)
    return RuntimeToolContext(
        ytdlp_executable=ytdlp_executable,
        ffmpeg_location=ffmpeg_location,
        deno_executable=deno_executable,
        ytdlp_version=_read_tool_version(Path(ytdlp_executable)),
        ffmpeg_version=_read_tool_version(Path(ffmpeg_location) / "ffmpeg.exe") if ffmpeg_location else None,
        deno_version=_read_tool_version(Path(deno_executable)) if deno_executable else None,
    )


def log_runtime_tool_context(context: RuntimeToolContext, log_callback: LogCallback) -> None:
    log_callback(f"yt-dlp: {context.ytdlp_executable}{_version_suffix(context.ytdlp_version)}")
    if context.ffmpeg_location:
        log_callback(f"ffmpeg: {context.ffmpeg_location}{_version_suffix(context.ffmpeg_version)}")
    else:
        log_callback("ffmpeg: not found")
    if context.deno_executable:
        log_callback(f"Deno: {context.deno_executable}{_version_suffix(context.deno_version)}")
        log_callback("YouTube JavaScript challenge support enabled with remote EJS components.")
    else:
        log_callback("Deno: not found; YouTube JavaScript challenge support disabled.")


def _read_tool_version(executable: Path) -> str | None:
    try:
        result = subprocess.run(
            [str(executable), "-version" if executable.name == "ffmpeg.exe" else "--version"],
            capture_output=True,
            text=True,
            check=False,
            timeout=10,
            **_hidden_subprocess_kwargs(),
        )
    except (OSError, subprocess.TimeoutExpired):
        return None
    if result.returncode != 0:
        return None
    first_line = (result.stdout or result.stderr).splitlines()
    return first_line[0].strip() if first_line else None


def _hidden_subprocess_kwargs() -> dict[str, int]:
    creationflags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    if creationflags:
        return {"creationflags": creationflags}
    return {}


def _context_paths_exist(context: RuntimeToolContext) -> bool:
    if not Path(context.ytdlp_executable).exists():
        return False
    if context.ffmpeg_location and not (
        (Path(context.ffmpeg_location) / "ffmpeg.exe").exists()
        and (Path(context.ffmpeg_location) / "ffprobe.exe").exists()
    ):
        return False
    if context.deno_executable and not Path(context.deno_executable).exists():
        return False
    return True


def _version_suffix(version: str | None) -> str:
    return f" ({version})" if version else ""
