from __future__ import annotations

import json
import os
import shutil
import sys
from dataclasses import asdict, dataclass
from pathlib import Path


APP_NAME = "YT-DLP Helper"
SETTINGS_FILE = "settings.json"
ARCHIVE_FILE = "download-archive.txt"
COOKIES_FILE = "cookies.txt"


@dataclass
class Settings:
    preset: str = "best-video"
    download_dir: str = ""


@dataclass(frozen=True)
class AppPaths:
    data_dir: Path
    settings_file: Path
    archive_file: Path
    cookies_file: Path
    download_dir: Path


def get_app_paths() -> AppPaths:
    local_appdata = Path(os.environ.get("LOCALAPPDATA", Path.home() / "AppData" / "Local"))
    downloads_root = Path.home() / "Downloads"
    data_dir = local_appdata / APP_NAME
    download_dir = downloads_root / APP_NAME
    return AppPaths(
        data_dir=data_dir,
        settings_file=data_dir / SETTINGS_FILE,
        archive_file=data_dir / ARCHIVE_FILE,
        cookies_file=data_dir / COOKIES_FILE,
        download_dir=download_dir,
    )


def ensure_app_dirs(paths: AppPaths) -> None:
    paths.data_dir.mkdir(parents=True, exist_ok=True)
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
    )
    if not settings.download_dir:
        settings.download_dir = str(paths.download_dir)
    return settings


def save_settings(paths: AppPaths, settings: Settings) -> None:
    ensure_app_dirs(paths)
    paths.settings_file.write_text(
        json.dumps(asdict(settings), indent=2),
        encoding="utf-8",
    )


def find_ffmpeg_location() -> str | None:
    candidates: list[Path] = []

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
        if (candidate / "ffmpeg.exe").exists():
            return str(candidate)

    return None


def find_ytdlp_executable() -> str | None:
    candidates: list[Path] = []

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
