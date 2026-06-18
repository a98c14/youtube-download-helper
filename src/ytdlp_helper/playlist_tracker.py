from __future__ import annotations

import json
import re
import subprocess
from urllib.parse import parse_qs, urlencode, urlparse

from .config import AppPaths, find_ytdlp_executable
from .downloader import _hidden_subprocess_kwargs


def parse_youtube_playlist_id(url: str) -> str | None:
    try:
        parsed = urlparse(url.strip())
    except ValueError:
        return None
    if parsed.scheme not in {"http", "https"} or parsed.hostname not in {
        "youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com", "youtu.be"
    }:
        return None
    value = parse_qs(parsed.query).get("list", [""])[0].strip()
    return value if re.fullmatch(r"[A-Za-z0-9_-]{10,}", value) else None


def canonical_playlist_url(playlist_id: str) -> str:
    return "https://www.youtube.com/playlist?" + urlencode({"list": playlist_id})


class PlaylistChecker:
    def __init__(self, paths: AppPaths) -> None:
        self.paths = paths

    def check(self, url: str) -> tuple[str, list[dict[str, object]]]:
        executable = find_ytdlp_executable(self.paths)
        if not executable:
            raise RuntimeError("yt-dlp.exe was not found. Use Help > Update and try again.")
        command = [executable, "--flat-playlist", "--dump-single-json", "--no-warnings"]
        if self.paths.cookies_file.exists():
            command.extend(["--cookies", str(self.paths.cookies_file)])
        command.append(url)
        result = subprocess.run(command, capture_output=True, text=True, check=False, **_hidden_subprocess_kwargs())
        if result.returncode:
            raise RuntimeError(result.stderr.strip() or "Playlist check failed.")
        payload = json.loads(result.stdout)
        entries = []
        for index, entry in enumerate(payload.get("entries") or [], start=1):
            if not isinstance(entry, dict) or not entry.get("id"):
                continue
            entries.append({
                "video_id": str(entry["id"]), "title": str(entry.get("title") or entry["id"]),
                "position": entry.get("playlist_index") or index,
                "upload_date": str(entry.get("upload_date") or ""),
            })
        return str(payload.get("title") or ""), entries
