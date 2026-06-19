from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime
from enum import Enum
from pathlib import Path

from .config import AppPaths


class CookiePhase(Enum):
    NONE = "none"
    SAVED = "saved"


@dataclass(frozen=True)
class CookieStatus:
    phase: CookiePhase
    timestamp: str | None = None


def is_valid_netscape_cookie_text(text: str) -> bool:
    for raw_line in text.splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#"):
            continue
        if len(raw_line.split("\t")) == 7:
            return True
    return False


def save_cookie_text(paths: AppPaths, text: str) -> None:
    if not is_valid_netscape_cookie_text(text):
        raise ValueError("Paste Netscape-format cookies.txt text before downloading.")

    paths.data_dir.mkdir(parents=True, exist_ok=True)
    paths.cookies_file.write_text(text, encoding="utf-8")


def get_cookie_status(paths: AppPaths) -> CookieStatus:
    if not paths.cookies_file.exists():
        return CookieStatus(CookiePhase.NONE)

    modified = datetime.fromtimestamp(paths.cookies_file.stat().st_mtime)
    return CookieStatus(CookiePhase.SAVED, timestamp=modified.strftime("%Y-%m-%d %H:%M:%S"))


def has_saved_cookies(paths: AppPaths) -> bool:
    return paths.cookies_file.exists()
