from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import sys
import tempfile
import urllib.error
import urllib.request
import zipfile
from dataclasses import dataclass
from pathlib import Path
from typing import Callable

from . import __version__
from .dependencies import _download_file
from .worker_status import AppUpdatePhase, AppUpdateStatus, StatusEvent, WorkerPhase


LogCallback = Callable[[str], None]
StatusCallback = Callable[[WorkerPhase, StatusEvent], None]

LATEST_RELEASE_URL = "https://api.github.com/repos/a98c14/youtube-download-helper/releases/latest"
PORTABLE_ZIP_PATTERN = re.compile(r"^YouTube-Download-Helper-.+-windows-portable\.zip$")
APP_EXE_NAME = "YouTube Download Helper.exe"


class AppUpdateError(RuntimeError):
    pass


@dataclass(frozen=True)
class ReleaseAsset:
    name: str
    download_url: str


@dataclass(frozen=True)
class AppUpdateResult:
    message: str
    restart_script: Path | None = None

    @property
    def restart_ready(self) -> bool:
        return self.restart_script is not None


def check_and_stage_app_update(
    log_callback: LogCallback,
    status_callback: StatusCallback,
    current_version: str = __version__,
) -> AppUpdateResult:
    status_callback("installing", AppUpdateStatus(AppUpdatePhase.CHECKING))
    release = _fetch_latest_release()
    tag_name = str(release.get("tag_name", "")).strip()
    if not tag_name:
        raise AppUpdateError("Latest release did not include a version tag.")

    if not _is_newer_version(tag_name, current_version):
        log_callback(f"App is current at {current_version}; latest stable release is {tag_name}.")
        return AppUpdateResult("Runtime tools updated. App is already current.")

    if not _is_frozen_portable():
        log_callback(f"App update {tag_name} is available, but self-update is only supported in the packaged app.")
        return AppUpdateResult("Runtime tools updated. App update is only available in the packaged app.")

    zip_asset, checksum_asset = select_portable_assets(release)
    status_callback("installing", AppUpdateStatus(AppUpdatePhase.DOWNLOADING))
    log_callback(f"Downloading app update {zip_asset.name}")

    with tempfile.TemporaryDirectory(prefix="ytdlp-helper-app-update-", delete=False) as temp_dir:
        temp_path = Path(temp_dir)
        zip_path = temp_path / zip_asset.name
        checksum_path = temp_path / checksum_asset.name
        stage_dir = temp_path / "staged"
        _download_file(zip_asset.download_url, zip_path, None, log_callback, status_callback)
        _download_file(checksum_asset.download_url, checksum_path, None, log_callback, status_callback)
        _verify_sha256(zip_path, checksum_path)
        _extract_and_validate(zip_path, stage_dir)
        script_path = _write_restart_script(
            temp_path,
            current_pid=os.getpid(),
            install_dir=Path(sys.executable).resolve().parent,
            staged_dir=stage_dir,
            executable_name=APP_EXE_NAME,
        )

    status_callback("installing", AppUpdateStatus(AppUpdatePhase.READY))
    log_callback(f"App update {tag_name} is staged at {stage_dir}")
    return AppUpdateResult("Runtime tools updated. App update is ready to install after restart.", script_path)


def select_portable_assets(release: dict[str, object]) -> tuple[ReleaseAsset, ReleaseAsset]:
    assets = release.get("assets")
    if not isinstance(assets, list):
        raise AppUpdateError("Latest release did not include downloadable assets.")

    parsed_assets: list[ReleaseAsset] = []
    for asset in assets:
        if not isinstance(asset, dict):
            continue
        name = str(asset.get("name", "")).strip()
        download_url = str(asset.get("browser_download_url", "")).strip()
        if name and download_url:
            parsed_assets.append(ReleaseAsset(name=name, download_url=download_url))

    zip_assets = [asset for asset in parsed_assets if PORTABLE_ZIP_PATTERN.match(asset.name)]
    if not zip_assets:
        raise AppUpdateError("Latest release is missing the Windows portable ZIP asset.")
    if len(zip_assets) > 1:
        raise AppUpdateError("Latest release has multiple Windows portable ZIP assets.")

    zip_asset = zip_assets[0]
    checksum_name = f"{zip_asset.name}.sha256"
    checksum_asset = next((asset for asset in parsed_assets if asset.name == checksum_name), None)
    if not checksum_asset:
        raise AppUpdateError("Latest release is missing the portable ZIP SHA256 checksum asset.")
    return zip_asset, checksum_asset


def start_restart_script(script_path: Path) -> None:
    import subprocess

    subprocess.Popen(
        [
            "powershell",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            str(script_path),
        ],
        cwd=str(script_path.parent),
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
    )


def _fetch_latest_release() -> dict[str, object]:
    request = urllib.request.Request(LATEST_RELEASE_URL, headers={"User-Agent": "YT-DLP Helper"})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            payload = response.read()
    except urllib.error.URLError as exc:
        raise AppUpdateError("Could not check the latest app release. Check your internet connection.") from exc

    try:
        data = json.loads(payload.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise AppUpdateError("GitHub returned an invalid release response.") from exc
    if not isinstance(data, dict):
        raise AppUpdateError("GitHub returned an invalid release response.")
    return data


def _is_newer_version(tag_name: str, current_version: str) -> bool:
    return _version_tuple(tag_name) > _version_tuple(current_version)


def _version_tuple(version: str) -> tuple[int, int, int]:
    normalized = version.strip()
    if normalized.startswith("v"):
        normalized = normalized[1:]
    parts = normalized.split(".")
    if len(parts) != 3:
        raise AppUpdateError(f"Unsupported release version: {version}")
    try:
        return tuple(int(part) for part in parts)  # type: ignore[return-value]
    except ValueError as exc:
        raise AppUpdateError(f"Unsupported release version: {version}") from exc


def _verify_sha256(zip_path: Path, checksum_path: Path) -> None:
    expected = _read_sha256(checksum_path)
    actual = hashlib.sha256(zip_path.read_bytes()).hexdigest()
    if actual.lower() != expected.lower():
        raise AppUpdateError("Downloaded app update did not match its SHA256 checksum.")


def _read_sha256(checksum_path: Path) -> str:
    text = checksum_path.read_text(encoding="utf-8").strip()
    match = re.search(r"\b[a-fA-F0-9]{64}\b", text)
    if not match:
        raise AppUpdateError("The app update checksum file did not contain a SHA256 hash.")
    return match.group(0)


def _extract_and_validate(zip_path: Path, stage_dir: Path) -> None:
    try:
        with zipfile.ZipFile(zip_path) as archive:
            archive.extractall(stage_dir)
    except zipfile.BadZipFile as exc:
        raise AppUpdateError("Downloaded app update ZIP could not be opened.") from exc

    if not (stage_dir / APP_EXE_NAME).is_file():
        shutil.rmtree(stage_dir, ignore_errors=True)
        raise AppUpdateError(f"Downloaded app update did not contain {APP_EXE_NAME}.")


def _is_frozen_portable() -> bool:
    return bool(getattr(sys, "frozen", False)) and Path(sys.executable).name.lower().endswith(".exe")


def _write_restart_script(
    script_dir: Path,
    current_pid: int,
    install_dir: Path,
    staged_dir: Path,
    executable_name: str,
) -> Path:
    backup_dir = script_dir / "backup"
    script_path = script_dir / "apply-update.ps1"
    lines = [
        "$ErrorActionPreference = 'Stop'",
        f"$pidToWait = {current_pid}",
        f"$installDir = {quote_powershell(install_dir)}",
        f"$stagedDir = {quote_powershell(staged_dir)}",
        f"$backupDir = {quote_powershell(backup_dir)}",
        f"$exeName = {quote_powershell(executable_name)}",
        "Wait-Process -Id $pidToWait -ErrorAction SilentlyContinue",
        "if (Test-Path -LiteralPath $backupDir) { Remove-Item -LiteralPath $backupDir -Recurse -Force }",
        "New-Item -ItemType Directory -Path $backupDir | Out-Null",
        "try {",
        "  Get-ChildItem -LiteralPath $installDir -Force | ForEach-Object {",
        "    Move-Item -LiteralPath $_.FullName -Destination $backupDir -Force",
        "  }",
        "  Get-ChildItem -LiteralPath $stagedDir -Force | ForEach-Object {",
        "    Copy-Item -LiteralPath $_.FullName -Destination $installDir -Recurse -Force",
        "  }",
        "  Start-Process -FilePath (Join-Path $installDir $exeName)",
        "  Remove-Item -LiteralPath $backupDir -Recurse -Force",
        "  Remove-Item -LiteralPath $stagedDir -Recurse -Force",
        "} catch {",
        "  if (Test-Path -LiteralPath $backupDir) {",
        "    Get-ChildItem -LiteralPath $installDir -Force | Remove-Item -Recurse -Force",
        "    Get-ChildItem -LiteralPath $backupDir -Force | ForEach-Object {",
        "      Move-Item -LiteralPath $_.FullName -Destination $installDir -Force",
        "    }",
        "  }",
        "  throw",
        "}",
    ]
    script_path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return script_path


def quote_powershell(path_or_value: Path | str) -> str:
    return "'" + str(path_or_value).replace("'", "''") + "'"
