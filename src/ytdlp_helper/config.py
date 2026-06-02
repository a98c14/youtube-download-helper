from __future__ import annotations

import json
import os
import shutil
import sys
from dataclasses import asdict, dataclass
from pathlib import Path

from .i18n import normalize_language


APP_NAME = "YT-DLP Helper"
DEFAULT_DOWNLOAD_FOLDER_NAME = "youtube-download-helper"
SETTINGS_FILE = "settings.json"
ARCHIVE_FILE = "download-archive.txt"
COOKIES_FILE = "cookies.txt"
LOGS_FOLDER_NAME = "logs"
ACTIVITY_LOG_FILE = "activity.log"
DEFAULT_FILENAME_TEMPLATE = "%(title)s [%(id)s].%(ext)s"
DEFAULT_QUEUE_CONCURRENCY = 1
MIN_QUEUE_CONCURRENCY = 1
MAX_QUEUE_CONCURRENCY = 4


@dataclass
class Settings:
    preset: str = "best-video"
    download_dir: str = ""
    language: str = "tr"
    filename_template: str = DEFAULT_FILENAME_TEMPLATE
    queue_concurrency: int = DEFAULT_QUEUE_CONCURRENCY


@dataclass(frozen=True)
class AppPaths:
    data_dir: Path
    settings_file: Path
    archive_file: Path
    cookies_file: Path
    logs_dir: Path
    activity_log_file: Path
    tools_dir: Path
    ytdlp_executable: Path
    ffmpeg_dir: Path
    ffmpeg_executable: Path
    ffprobe_executable: Path
    deno_dir: Path
    deno_executable: Path
    download_dir: Path


def get_app_paths() -> AppPaths:
    local_appdata = Path(os.environ.get("LOCALAPPDATA", Path.home() / "AppData" / "Local"))
    downloads_root = Path.home() / "Downloads"
    data_dir = local_appdata / APP_NAME
    download_dir = downloads_root / DEFAULT_DOWNLOAD_FOLDER_NAME
    return AppPaths(
        data_dir=data_dir,
        settings_file=data_dir / SETTINGS_FILE,
        archive_file=data_dir / ARCHIVE_FILE,
        cookies_file=data_dir / COOKIES_FILE,
        logs_dir=data_dir / LOGS_FOLDER_NAME,
        activity_log_file=data_dir / LOGS_FOLDER_NAME / ACTIVITY_LOG_FILE,
        tools_dir=data_dir / "tools",
        ytdlp_executable=data_dir / "tools" / "yt-dlp.exe",
        ffmpeg_dir=data_dir / "tools" / "ffmpeg",
        ffmpeg_executable=data_dir / "tools" / "ffmpeg" / "ffmpeg.exe",
        ffprobe_executable=data_dir / "tools" / "ffmpeg" / "ffprobe.exe",
        deno_dir=data_dir / "tools" / "deno",
        deno_executable=data_dir / "tools" / "deno" / "deno.exe",
        download_dir=download_dir,
    )


def ensure_app_dirs(paths: AppPaths) -> None:
    paths.data_dir.mkdir(parents=True, exist_ok=True)
    paths.logs_dir.mkdir(parents=True, exist_ok=True)
    paths.tools_dir.mkdir(parents=True, exist_ok=True)
    paths.download_dir.mkdir(parents=True, exist_ok=True)
    paths.archive_file.touch(exist_ok=True)


def load_settings(paths: AppPaths) -> Settings:
    defaults = Settings(download_dir=str(paths.download_dir))
    if not paths.settings_file.exists():
        return defaults

    try:
        data = json.loads(paths.settings_file.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return defaults

    settings = Settings(
        preset=str(data.get("preset", defaults.preset)),
        download_dir=str(data.get("download_dir", defaults.download_dir)),
        language=normalize_language(str(data.get("language", defaults.language))),
        filename_template=str(data.get("filename_template", defaults.filename_template)),
        queue_concurrency=_normalize_queue_concurrency(data.get("queue_concurrency", defaults.queue_concurrency)),
    )
    if not settings.download_dir:
        settings.download_dir = str(paths.download_dir)
    if not settings.filename_template.strip():
        settings.filename_template = DEFAULT_FILENAME_TEMPLATE
    return settings


def save_settings(paths: AppPaths, settings: Settings) -> None:
    ensure_app_dirs(paths)
    paths.settings_file.write_text(
        json.dumps(asdict(settings), indent=2),
        encoding="utf-8",
    )


def _normalize_queue_concurrency(value: object) -> int:
    try:
        concurrency = int(value)
    except (TypeError, ValueError):
        return DEFAULT_QUEUE_CONCURRENCY
    if MIN_QUEUE_CONCURRENCY <= concurrency <= MAX_QUEUE_CONCURRENCY:
        return concurrency
    return DEFAULT_QUEUE_CONCURRENCY


def find_ffmpeg_location(paths: AppPaths | None = None) -> str | None:
    candidates: list[Path] = []

    if paths and paths.ffmpeg_executable.exists() and paths.ffprobe_executable.exists():
        return str(paths.ffmpeg_dir)

    if getattr(sys, "frozen", False):
        exe_dir = Path(sys.executable).resolve().parent
        candidates.extend(
            [
                exe_dir / "ffmpeg",
                exe_dir / "_internal" / "ffmpeg",
            ]
        )
    else:
        project_root = Path(__file__).resolve().parents[2]
        candidates.extend(
            [
                project_root / "vendor" / "ffmpeg",
                project_root / "ffmpeg",
            ]
        )

    for candidate in candidates:
        if _has_ffmpeg_pair(candidate):
            return str(candidate)

    path_ffmpeg = shutil.which("ffmpeg.exe") or shutil.which("ffmpeg")
    path_ffprobe = shutil.which("ffprobe.exe") or shutil.which("ffprobe")
    if path_ffmpeg and path_ffprobe:
        ffmpeg_dir = Path(path_ffmpeg).parent
        ffprobe_dir = Path(path_ffprobe).parent
        if ffmpeg_dir == ffprobe_dir:
            return str(ffmpeg_dir)

    return None


def find_ytdlp_executable(paths: AppPaths | None = None) -> str | None:
    candidates: list[Path] = []

    if paths and paths.ytdlp_executable.exists():
        return str(paths.ytdlp_executable)

    if getattr(sys, "frozen", False):
        exe_dir = Path(sys.executable).resolve().parent
        candidates.extend(
            [
                exe_dir / "yt-dlp.exe",
                exe_dir / "_internal" / "yt-dlp.exe",
            ]
        )
    else:
        project_root = Path(__file__).resolve().parents[2]
        candidates.extend(
            [
                project_root / "vendor" / "yt-dlp.exe",
                project_root / "vendor" / "yt-dlp" / "yt-dlp.exe",
            ]
        )

    for candidate in candidates:
        if candidate.exists():
            return str(candidate)

    return shutil.which("yt-dlp.exe") or shutil.which("yt-dlp")


def find_deno_executable(paths: AppPaths | None = None) -> str | None:
    candidates: list[Path] = []

    if paths and paths.deno_executable.exists():
        return str(paths.deno_executable)

    if getattr(sys, "frozen", False):
        exe_dir = Path(sys.executable).resolve().parent
        candidates.extend(
            [
                exe_dir / "deno" / "deno.exe",
                exe_dir / "_internal" / "deno" / "deno.exe",
            ]
        )
    else:
        project_root = Path(__file__).resolve().parents[2]
        candidates.extend(
            [
                project_root / "vendor" / "deno" / "deno.exe",
            ]
        )

    for candidate in candidates:
        if candidate.exists():
            return str(candidate)

    return None


def _has_ffmpeg_pair(directory: Path) -> bool:
    return (directory / "ffmpeg.exe").exists() and (directory / "ffprobe.exe").exists()
