# YouTube Download Helper

A small Windows desktop app that wraps `yt-dlp` in a simple GUI for non-technical users.

## Features

- Paste a YouTube URL and download with one click
- Supports single videos and playlists
- Uses pasted Netscape-format `cookies.txt` text for paid/member content
- Includes a download archive to skip videos that were already downloaded
- Editable output folder, defaulting to `%USERPROFILE%\Downloads\youtube-download-helper`
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
- `yt-dlp.exe` available from `vendor\yt-dlp.exe`, `vendor\yt-dlp\yt-dlp.exe`, or `PATH` for local runs
- `ffmpeg` available on `PATH` for local runs, or bundled into `dist\YouTube Download Helper\ffmpeg\` for portable builds

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

The build produces a portable folder with one launcher executable plus bundled `yt-dlp.exe` and `ffmpeg` sidecars.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build_portable.ps1
```

Expected output:

- `dist\YouTube Download Helper\YouTube Download Helper.exe`
- `dist\YouTube Download Helper\yt-dlp.exe`
- `dist\YouTube Download Helper\ffmpeg\ffmpeg.exe`
- `dist\YouTube Download Helper\ffmpeg\ffprobe.exe`

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
