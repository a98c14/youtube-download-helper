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
DEFAULT_FILENAME_TEMPLATE = "%(title)s.%(ext)s"
LEGACY_FILENAME_TEMPLATE = "%(title)s [%(id)s].%(ext)s"
DEFAULT_QUEUE_CONCURRENCY = 1
MIN_QUEUE_CONCURRENCY = 1
MAX_QUEUE_CONCURRENCY = 4
DEFAULT_CATEGORY_ID = "default"


@dataclass(frozen=True)
class Category:
    id: str
    name: str
    download_dir: str


@dataclass
class Settings:
    preset: str = "best-video"
    download_dir: str = ""
    language: str = "tr"
    filename_template: str = DEFAULT_FILENAME_TEMPLATE
    queue_concurrency: int = DEFAULT_QUEUE_CONCURRENCY
    organize_by_channel: bool = True
    categories: list[Category] | None = None
    selected_category_id: str = ""


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


def load_settings(paths: AppPaths) -> Settings:
    default_category = Category(DEFAULT_CATEGORY_ID, "Default", str(paths.download_dir))
    defaults = Settings(
        download_dir=str(paths.download_dir),
        categories=[default_category],
        selected_category_id=default_category.id,
    )
    if not paths.settings_file.exists():
        return defaults

    try:
        data = json.loads(paths.settings_file.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return defaults

    download_dir = str(data.get("download_dir", defaults.download_dir)) or defaults.download_dir
    categories = _normalize_categories(data.get("categories"), download_dir)
    selected_category_id = str(data.get("selected_category_id", ""))
    if not any(category.id == selected_category_id for category in categories):
        selected_category_id = categories[0].id
    settings = Settings(
        preset=str(data.get("preset", defaults.preset)),
        download_dir=download_dir,
        language=normalize_language(str(data.get("language", defaults.language))),
        filename_template=str(data.get("filename_template", defaults.filename_template)),
        queue_concurrency=_normalize_queue_concurrency(data.get("queue_concurrency", defaults.queue_concurrency)),
        organize_by_channel=_normalize_bool(data.get("organize_by_channel", defaults.organize_by_channel), True),
        categories=categories,
        selected_category_id=selected_category_id,
    )
    if not settings.download_dir:
        settings.download_dir = str(paths.download_dir)
    if not settings.filename_template.strip():
        settings.filename_template = DEFAULT_FILENAME_TEMPLATE
    elif settings.filename_template.strip() == LEGACY_FILENAME_TEMPLATE:
        settings.filename_template = DEFAULT_FILENAME_TEMPLATE
    return settings


def settings_categories(settings: Settings, default_download_dir: str) -> list[Category]:
    return _normalize_categories(settings.categories, default_download_dir)


def _normalize_categories(value: object, default_download_dir: str) -> list[Category]:
    categories: list[Category] = []
    if isinstance(value, list):
        seen_ids: set[str] = set()
        for raw in value:
            if not isinstance(raw, dict):
                continue
            category_id = str(raw.get("id", "")).strip()
            name = str(raw.get("name", "")).strip()
            download_dir = str(raw.get("download_dir", "")).strip()
            if not category_id or category_id in seen_ids or not name or not download_dir:
                continue
            categories.append(Category(category_id, name, download_dir))
            seen_ids.add(category_id)
    if not categories:
        categories.append(Category(DEFAULT_CATEGORY_ID, "Default", default_download_dir))
    return categories


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


def _normalize_bool(value: object, default: bool) -> bool:
    if isinstance(value, bool):
        return value
    return default


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


def factory_reset(paths: AppPaths) -> list[str]:
    """Clear app-owned state, preserving downloaded media and Runtime Tools.

    Returns a list of error messages (empty on success).
    """
    errors: list[str] = []

    # --- Delete database and WAL/SHM companions ---
    for suffix in ("", "-wal", "-shm"):
        db_path = Path(str(paths.data_dir / "app.db") + suffix)
        if db_path.exists():
            try:
                db_path.unlink()
            except OSError as exc:
                errors.append(f"Could not delete {db_path.name}: {exc}")

    # --- Delete cookies ---
    if paths.cookies_file.exists():
        try:
            paths.cookies_file.unlink()
        except OSError as exc:
            errors.append(f"Could not delete cookies: {exc}")

    # --- Delete settings ---
    if paths.settings_file.exists():
        try:
            paths.settings_file.unlink()
        except OSError as exc:
            errors.append(f"Could not delete settings: {exc}")

    # --- Delete legacy queue files ---
    for queue_filename in ("queue.json", "queue.json.migrated"):
        queue_path = paths.data_dir / queue_filename
        if queue_path.exists():
            try:
                queue_path.unlink()
            except OSError as exc:
                errors.append(f"Could not delete {queue_filename}: {exc}")

    # --- Delete all log files ---
    if paths.logs_dir.exists():
        try:
            for log_file in paths.logs_dir.iterdir():
                try:
                    log_file.unlink()
                except OSError as exc:
                    errors.append(f"Could not delete log {log_file.name}: {exc}")
        except OSError as exc:
            errors.append(f"Could not list logs directory: {exc}")

    # --- Recreate directories and default state ---
    try:
        ensure_app_dirs(paths)
    except OSError as exc:
        errors.append(f"Could not recreate app directories: {exc}")

    # --- Create default settings file ---
    try:
        settings = Settings(
            download_dir=str(paths.download_dir),
            categories=[Category(DEFAULT_CATEGORY_ID, "Default", str(paths.download_dir))],
            selected_category_id=DEFAULT_CATEGORY_ID,
        )
        save_settings(paths, settings)
    except (OSError, ValueError) as exc:
        errors.append(f"Could not create default settings: {exc}")

    return errors
