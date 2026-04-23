from __future__ import annotations

import re
from dataclasses import dataclass
from pathlib import Path
from typing import Callable

from yt_dlp import DownloadError, YoutubeDL

from .config import AppPaths, find_ffmpeg_location


StatusCallback = Callable[[str, str], None]
LogCallback = Callable[[str], None]


@dataclass(frozen=True)
class DownloadRequest:
    url: str
    preset: str
    browser: str
    profile: str


class DownloadService:
    def __init__(self, paths: AppPaths) -> None:
        self._paths = paths

    def download(
        self,
        request: DownloadRequest,
        status_callback: StatusCallback,
        log_callback: LogCallback,
    ) -> None:
        url = request.url.strip()
        if not url:
            raise ValueError("Enter a YouTube video or playlist URL.")
        if not re.match(r"^https?://", url, flags=re.IGNORECASE):
            raise ValueError("Enter a valid URL starting with http:// or https://.")

        status_callback("queued", "Preparing download")
        options = self._build_options(request, status_callback, log_callback)

        try:
            with YoutubeDL(options) as ydl:
                status_callback("resolving", "Resolving video information")
                ydl.download([url])
        except DownloadError as exc:
            message = _humanize_error(str(exc))
            status_callback("failed", message)
            raise RuntimeError(message) from exc

    def _build_options(
        self,
        request: DownloadRequest,
        status_callback: StatusCallback,
        log_callback: LogCallback,
    ) -> dict:
        ffmpeg_location = find_ffmpeg_location()

        options = {
            "paths": {"home": str(self._paths.download_dir)},
            "outtmpl": {
                "default": "%(title)s [%(id)s].%(ext)s",
                "pl_video": "%(playlist)s/%(title)s [%(id)s].%(ext)s",
            },
            "windowsfilenames": True,
            "noplaylist": False,
            "ignoreerrors": False,
            "no_warnings": True,
            "download_archive": str(self._paths.archive_file),
            "cookiesfrombrowser": (request.browser, None, None, request.profile),
            "logger": _YtdlpLogger(status_callback, log_callback),
            "progress_hooks": [_progress_hook(status_callback)],
            "restrictfilenames": False,
            "quiet": True,
        }

        if ffmpeg_location:
            options["ffmpeg_location"] = ffmpeg_location

        if request.preset == "best-video":
            options["format"] = "bv*+ba/b"
            options["merge_output_format"] = "mp4"
        elif request.preset == "audio-mp3":
            options["format"] = "bestaudio/best"
            options["postprocessors"] = [
                {"key": "FFmpegExtractAudio", "preferredcodec": "mp3", "preferredquality": "192"}
            ]
        elif request.preset == "audio-m4a":
            options["format"] = "bestaudio[ext=m4a]/bestaudio/best"
        else:
            raise ValueError(f"Unsupported preset: {request.preset}")

        return options


def _progress_hook(status_callback: StatusCallback):
    def hook(data: dict) -> None:
        status = data.get("status")
        if status == "downloading":
            downloaded_bytes = data.get("downloaded_bytes") or 0
            total_bytes = data.get("total_bytes") or data.get("total_bytes_estimate") or 0
            if total_bytes:
                percent = int(downloaded_bytes * 100 / total_bytes)
                status_callback("downloading", f"Downloading {percent}%")
            else:
                status_callback("downloading", "Downloading")
        elif status == "finished":
            status_callback("postprocessing", "Finalizing file")

    return hook


def _humanize_error(message: str) -> str:
    lowered = message.lower()
    if "sign in" in lowered or "cookies" in lowered or "members-only" in lowered or "premium" in lowered:
        return "This video needs an entitled browser profile. Pick the logged-in Chrome or Edge profile and try again."
    if "unsupported url" in lowered or "unable to extract" in lowered:
        return "This URL could not be processed by yt-dlp."
    return message


class _YtdlpLogger:
    def __init__(self, status_callback: StatusCallback, log_callback: LogCallback) -> None:
        self._status_callback = status_callback
        self._log_callback = log_callback

    def debug(self, msg: str) -> None:
        self._log_callback(msg)
        lowered = msg.lower()
        if "has already been recorded in the archive" in lowered:
            self._status_callback("skipped", "Already downloaded; skipped by archive")
        elif "[download] downloading playlist" in lowered:
            self._status_callback("resolving", "Resolving playlist")

    def warning(self, msg: str) -> None:
        self._log_callback(f"Warning: {msg}")

    def error(self, msg: str) -> None:
        self._log_callback(f"Error: {msg}")
