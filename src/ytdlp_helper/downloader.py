from __future__ import annotations

import re
import subprocess
from dataclasses import dataclass
from pathlib import Path
from typing import Callable

from .config import (
    DEFAULT_FILENAME_TEMPLATE,
    AppPaths,
    find_deno_executable,
    find_ffmpeg_location,
    find_ytdlp_executable,
)
from .dependencies import ensure_runtime_tools, read_tool_version


StatusCallback = Callable[[str, str], None]
LogCallback = Callable[[str], None]

VIDEO_PRESET_FORMATS = {
    "video-1080p": "bv*[height<=1080]+ba/b[height<=1080]",
    "video-720p": "bv*[height<=720]+ba/b[height<=720]",
    "video-480p": "bv*[height<=480]+ba/b[height<=480]",
}


@dataclass(frozen=True)
class DownloadRequest:
    url: str
    preset: str
    playlist: bool = False


class DownloadService:
    def __init__(self, paths: AppPaths, filename_template: str = DEFAULT_FILENAME_TEMPLATE) -> None:
        self._paths = paths
        self._filename_template = filename_template.strip() or DEFAULT_FILENAME_TEMPLATE

    def get_ytdlp_version(self) -> str:
        executable = self._require_ytdlp_executable()
        result = subprocess.run(
            [executable, "--version"],
            capture_output=True,
            text=True,
            check=False,
            **_hidden_subprocess_kwargs(),
        )
        if result.returncode != 0:
            raise RuntimeError("Could not read yt-dlp version.")
        return result.stdout.strip()

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
        ensure_runtime_tools(self._paths, log_callback, status_callback)
        self._log_runtime_context(request, log_callback)
        command = self._build_command(request)
        if not self._paths.cookies_file.exists():
            log_callback("No saved cookies; downloading as public session.")
        log_callback(f"Running: {command[0]} ...")
        status_callback("resolving", "Resolving video information")
        output = self._run_process(command, log_callback, status_callback)

        if _process_failed(output):
            message = _humanize_error("\n".join(output))
            status_callback("failed", message)
            raise RuntimeError(message)

    def _build_command(self, request: DownloadRequest) -> list[str]:
        executable = self._require_ytdlp_executable()
        ffmpeg_location = find_ffmpeg_location(self._paths)
        deno_executable = find_deno_executable(self._paths)
        command = [
            executable,
            "--paths",
            f"home:{self._paths.download_dir}",
            "--windows-filenames",
            "--yes-playlist" if request.playlist else "--no-playlist",
            "--no-warnings",
            "--newline",
            "--no-color",
            "--download-archive",
            str(self._paths.archive_file),
        ]
        if request.playlist:
            command.extend(
                [
                    "--output",
                    f"default:{self._filename_template}",
                    "--output",
                    f"pl_video:%(playlist)s/{self._filename_template}",
                ]
            )
        else:
            command.extend(["--output", self._filename_template])

        if self._paths.cookies_file.exists():
            command.extend(["--cookies", str(self._paths.cookies_file)])

        if ffmpeg_location:
            command.extend(["--ffmpeg-location", ffmpeg_location])

        if deno_executable:
            command.extend(["--js-runtimes", f"deno:{deno_executable}", "--remote-components", "ejs:github"])

        if request.preset == "best-video":
            command.extend(["--format", "bv*+ba/b", "--merge-output-format", "mp4"])
        elif request.preset in VIDEO_PRESET_FORMATS:
            command.extend(
                ["--format", VIDEO_PRESET_FORMATS[request.preset], "--merge-output-format", "mp4"]
            )
        elif request.preset == "audio-mp3":
            command.extend(
                [
                    "--format",
                    "bestaudio/best",
                    "--extract-audio",
                    "--audio-format",
                    "mp3",
                    "--audio-quality",
                    "192K",
                ]
            )
        elif request.preset == "audio-m4a":
            command.extend(["--format", "bestaudio[ext=m4a]/bestaudio/best"])
        else:
            raise ValueError(f"Unsupported preset: {request.preset}")

        command.append(request.url.strip())
        return command

    def _require_ytdlp_executable(self) -> str:
        executable = find_ytdlp_executable(self._paths)
        if executable:
            return executable
        raise RuntimeError(
            "yt-dlp.exe was not found and could not be installed. Check your internet connection and try again."
        )

    def _log_runtime_context(self, request: DownloadRequest, log_callback: LogCallback) -> None:
        executable = find_ytdlp_executable(self._paths)
        ffmpeg_location = find_ffmpeg_location(self._paths)
        deno_executable = find_deno_executable(self._paths)

        if executable:
            log_callback(f"yt-dlp: {executable}{_version_suffix(executable)}")
        if ffmpeg_location:
            log_callback(f"ffmpeg: {ffmpeg_location}")
        else:
            log_callback("ffmpeg: not found")
        if deno_executable:
            log_callback(f"Deno: {deno_executable}{_version_suffix(deno_executable)}")
            log_callback("YouTube JavaScript challenge support enabled with remote EJS components.")
        else:
            log_callback("Deno: not found; YouTube JavaScript challenge support disabled.")
        log_callback(f"Preset: {request.preset}")

    def _run_process(
        self,
        command: list[str],
        log_callback: LogCallback,
        status_callback: StatusCallback | None = None,
    ) -> list[str]:
        try:
            process = subprocess.Popen(
                command,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                errors="replace",
                **_hidden_subprocess_kwargs(),
            )
        except OSError as exc:
            raise RuntimeError(f"Could not start yt-dlp: {exc}") from exc

        output: list[str] = []
        if process.stdout:
            for raw_line in process.stdout:
                line = raw_line.rstrip()
                if not line:
                    continue
                output.append(line)
                log_callback(line)
                if status_callback:
                    _update_status_from_output(line, status_callback)

        return_code = process.wait()
        if return_code != 0:
            output.append(f"yt-dlp exited with code {return_code}")
        return output


def _update_status_from_output(line: str, status_callback: StatusCallback) -> None:
    lowered = line.lower()
    if "has already been recorded in the archive" in lowered:
        status_callback("skipped", "Already downloaded; skipped by archive")
        return
    if "[download] downloading playlist" in lowered:
        status_callback("resolving", "Resolving playlist")
        return
    if "[merger]" in lowered or "[extractaudio]" in lowered or "post-process" in lowered:
        status_callback("postprocessing", "Finalizing file")
        return

    progress_match = re.search(r"\[download\]\s+(\d+(?:\.\d+)?)%", line)
    if progress_match:
        status_callback("downloading", f"Downloading {int(float(progress_match.group(1)))}%")
        speed_match = re.search(r"\bat\s+([^\s]+/s)\b", line)
        if speed_match:
            status_callback("speed", speed_match.group(1))


def _process_failed(output: list[str]) -> bool:
    return bool(output and output[-1].startswith("yt-dlp exited with code "))


def _humanize_error(message: str) -> str:
    lowered = message.lower()
    if (
        "requested format is not available" in lowered
        or "gvs po token" in lowered
        or "po token" in lowered
        or "sabr" in lowered
        or "only images are available" in lowered
    ):
        return (
            "YouTube did not provide playable video formats for this request. "
            "Click Help > Update to refresh runtime tools, paste fresh cookies, and try again. "
            "If it still fails, send the activity log with the error report."
        )
    if "sign in" in lowered or "cookie" in lowered or "members-only" in lowered or "premium" in lowered:
        return "This video needs fresh cookies from a logged-in browser. Paste updated cookies.txt text and try again."
    if "unsupported url" in lowered or "unable to extract" in lowered:
        return "This URL could not be processed by yt-dlp."
    return message


def _hidden_subprocess_kwargs() -> dict[str, int]:
    creationflags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    if creationflags:
        return {"creationflags": creationflags}
    return {}


def _version_suffix(executable: str) -> str:
    path = Path(executable)
    if not path.exists():
        return ""
    try:
        version = read_tool_version(path)
    except Exception:
        return ""
    return f" ({version})" if version else ""
