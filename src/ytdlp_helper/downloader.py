from __future__ import annotations

import re
import subprocess
from dataclasses import dataclass
from typing import Callable

from .config import AppPaths, find_ffmpeg_location, find_ytdlp_executable


StatusCallback = Callable[[str, str], None]
LogCallback = Callable[[str], None]


@dataclass(frozen=True)
class DownloadRequest:
    url: str
    preset: str


class DownloadService:
    def __init__(self, paths: AppPaths) -> None:
        self._paths = paths

    def get_ytdlp_version(self) -> str:
        executable = self._require_ytdlp_executable()
        result = subprocess.run(
            [executable, "--version"],
            capture_output=True,
            text=True,
            check=False,
        )
        if result.returncode != 0:
            raise RuntimeError("Could not read yt-dlp version.")
        return result.stdout.strip()

    def update_ytdlp(self, log_callback: LogCallback) -> str:
        executable = self._require_ytdlp_executable()
        log_callback(f"Current yt-dlp version: {self.get_ytdlp_version()}")
        log_callback(f"Running: {executable} -U")
        output = self._run_process([executable, "-U"], log_callback)

        if _pip_update_required(output):
            raise RuntimeError("This yt-dlp executable cannot self-update. Update it outside the app.")
        if _process_failed(output):
            raise RuntimeError("yt-dlp update failed. See the activity log for details.")

        return "yt-dlp updated. Restart the app before downloading again."

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
        ffmpeg_location = find_ffmpeg_location()
        command = [
            executable,
            "--paths",
            f"home:{self._paths.download_dir}",
            "--output",
            "default:%(title)s [%(id)s].%(ext)s",
            "--output",
            "pl_video:%(playlist)s/%(title)s [%(id)s].%(ext)s",
            "--windows-filenames",
            "--yes-playlist",
            "--no-warnings",
            "--newline",
            "--no-color",
            "--download-archive",
            str(self._paths.archive_file),
        ]

        if self._paths.cookies_file.exists():
            command.extend(["--cookies", str(self._paths.cookies_file)])

        if ffmpeg_location:
            command.extend(["--ffmpeg-location", ffmpeg_location])

        if request.preset == "best-video":
            command.extend(["--format", "bv*+ba/b", "--merge-output-format", "mp4"])
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
        executable = find_ytdlp_executable()
        if executable:
            return executable
        raise RuntimeError(
            "yt-dlp.exe was not found. Add yt-dlp.exe to the app folder, vendor folder, or PATH and try again."
        )

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

    match = re.search(r"\[download\]\s+(\d+(?:\.\d+)?)%", line)
    if match:
        status_callback("downloading", f"Downloading {int(float(match.group(1)))}%")


def _process_failed(output: list[str]) -> bool:
    return bool(output and output[-1].startswith("yt-dlp exited with code "))


def _pip_update_required(output: list[str]) -> bool:
    return any("installed yt-dlp with pip" in line.lower() for line in output)


def _humanize_error(message: str) -> str:
    lowered = message.lower()
    if "sign in" in lowered or "cookie" in lowered or "members-only" in lowered or "premium" in lowered:
        return "This video needs fresh cookies from a logged-in browser. Paste updated cookies.txt text and try again."
    if "unsupported url" in lowered or "unable to extract" in lowered:
        return "This URL could not be processed by yt-dlp."
    return message
