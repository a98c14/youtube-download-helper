from __future__ import annotations

from pathlib import Path
from urllib.parse import parse_qs, urlparse


ARCHIVE_EXTRACTOR = "youtube"
UNSUPPORTED_VIDEO_URL_MESSAGE = "Enter an individual YouTube video URL"


def parse_youtube_video_id(url: str) -> str | None:
    parsed = urlparse(url.strip())
    host = parsed.netloc.lower()
    path_parts = [part for part in parsed.path.split("/") if part]

    if not host:
        return None

    if _is_youtu_be_host(host):
        return path_parts[0] if path_parts else None

    if not _is_youtube_host(host):
        return None

    query = parse_qs(parsed.query)
    video_ids = query.get("v", [])
    if video_ids and video_ids[0]:
        return video_ids[0]

    if len(path_parts) >= 2 and path_parts[0].lower() in {"shorts", "embed", "live"}:
        return path_parts[1]

    return None


def is_archived(archive_file: Path, video_id: str) -> bool:
    if not archive_file.exists():
        return False

    lines = archive_file.read_text(encoding="utf-8").splitlines()
    return any(_is_matching_archive_line(line, video_id) for line in lines)


def clear_archive_entry(archive_file: Path, video_id: str) -> int:
    if not archive_file.exists():
        return 0

    lines = archive_file.read_text(encoding="utf-8").splitlines(keepends=True)
    kept_lines = [line for line in lines if not _is_matching_archive_line(line, video_id)]
    removed_count = len(lines) - len(kept_lines)

    if removed_count:
        archive_file.write_text("".join(kept_lines), encoding="utf-8")

    return removed_count


def _is_matching_archive_line(line: str, video_id: str) -> bool:
    parts = line.strip().split()
    return len(parts) == 2 and parts[0] == ARCHIVE_EXTRACTOR and parts[1] == video_id


def _is_youtube_host(host: str) -> bool:
    return host in {"youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com"}


def _is_youtu_be_host(host: str) -> bool:
    return host in {"youtu.be", "www.youtu.be"}
