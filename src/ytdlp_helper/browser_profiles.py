from __future__ import annotations

import json
import os
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class BrowserProfile:
    browser_key: str
    browser_label: str
    profile_id: str
    display_name: str
    path: Path


BROWSER_CONFIG = {
    "chrome": {
        "label": "Chrome",
        "user_data_dir": Path(os.environ.get("LOCALAPPDATA", "")) / "Google" / "Chrome" / "User Data",
    },
    "edge": {
        "label": "Edge",
        "user_data_dir": Path(os.environ.get("LOCALAPPDATA", "")) / "Microsoft" / "Edge" / "User Data",
    },
}


def list_supported_browsers() -> list[tuple[str, str]]:
    return [(key, value["label"]) for key, value in BROWSER_CONFIG.items()]


def discover_profiles(browser_key: str) -> list[BrowserProfile]:
    config = BROWSER_CONFIG.get(browser_key)
    if not config:
        return []

    user_data_dir = config["user_data_dir"]
    if not user_data_dir.exists():
        return []

    profile_names = _read_profile_names(user_data_dir)
    profiles: list[BrowserProfile] = []
    seen: set[str] = set()

    for child in sorted(user_data_dir.iterdir(), key=lambda item: item.name.lower()):
        if not child.is_dir():
            continue
        if child.name != "Default" and not child.name.startswith("Profile "):
            continue
        seen.add(child.name)
        profiles.append(
            BrowserProfile(
                browser_key=browser_key,
                browser_label=config["label"],
                profile_id=child.name,
                display_name=profile_names.get(child.name, child.name),
                path=child,
            )
        )

    # Ensure Default is represented even if profile directories are incomplete.
    if "Default" not in seen:
        default_dir = user_data_dir / "Default"
        profiles.insert(
            0,
            BrowserProfile(
                browser_key=browser_key,
                browser_label=config["label"],
                profile_id="Default",
                display_name=profile_names.get("Default", "Default"),
                path=default_dir,
            ),
        )

    return profiles


def _read_profile_names(user_data_dir: Path) -> dict[str, str]:
    local_state = user_data_dir / "Local State"
    if not local_state.exists():
        return {}

    try:
        data = json.loads(local_state.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return {}

    profile_cache = data.get("profile", {}).get("info_cache", {})
    results: dict[str, str] = {}
    for profile_id, info in profile_cache.items():
        name = info.get("name")
        if isinstance(name, str) and name.strip():
            results[profile_id] = name.strip()
    return results

