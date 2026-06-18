# YouTube Download Helper

A small Windows desktop app that wraps `yt-dlp` in a simple GUI for non-technical users.

## Features

- Paste a YouTube URL and download with one click
- Supports single videos and playlists
- Uses pasted Netscape-format `cookies.txt` text for paid/member content
- Includes a download archive to skip videos that were already downloaded
- User-defined Categories route downloads to named destination folders
- Simple presets only:
  - `Best Video`
  - `Video 1080p`
  - `Video 720p`
  - `Video 480p`
  - `Audio MP3`
  - `Audio M4A`

## Requirements

- Windows
- Python 3.13+
- Internet access the first time a download or yt-dlp update needs missing runtime tools

## Run Locally

```powershell
python -m venv .venv
.venv\Scripts\Activate.ps1
python -m pip install -r requirements.txt
python -m ytdlp_helper
```

## Tests

```powershell
python -m unittest discover -s tests -v
```

## Build Portable App

The build produces a portable folder with one launcher executable. The app downloads `yt-dlp`, `ffmpeg`, and `ffprobe` into `%LOCALAPPDATA%\YT-DLP Helper\tools\` on first use when they are missing.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build_portable.ps1
```

Expected output:

- `dist\YouTube Download Helper\YouTube Download Helper.exe`

Zip the `dist\YouTube Download Helper` folder and share it. The user only needs to extract it and run the `.exe`.

## How Authenticated Downloads Work

- Install the Chrome/Edge extension `Get cookies.txt LOCALLY`.
- In a logged-in browser session with access to the content, use the extension to copy Netscape-format cookies for YouTube.
- In the app, click `Paste Cookies` to save the clipboard text to the app data folder.
- Premium/member downloads only work while the saved cookies are fresh and tied to an account with access.
- If a download starts failing with sign-in or cookie errors, copy fresh cookies from the logged-in browser and paste them again.

Extension source:

```text
https://chromewebstore.google.com/detail/get-cookiestxt-locally/cclelndahbckbenkjhflpdbgdldlbecc
```

## App Data

The app stores settings and the download archive under:

```text
%LOCALAPPDATA%\YT-DLP Helper\
```

Files:

- `settings.json`
- `download-archive.txt`
- `cookies.txt`
- `tools\yt-dlp.exe`
- `tools\ffmpeg\ffmpeg.exe`
- `tools\ffmpeg\ffprobe.exe`
- `tools\*.json` runtime tool metadata

`settings.json` stores Categories and the currently selected Category. Existing settings are migrated to a `Default` Category using the previously saved downloads folder. Queue items snapshot their Category name and destination, so later Category edits do not move queued or historical downloads.
