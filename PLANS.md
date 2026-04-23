# Product Requirements Document

## Product
YouTube Download Helper

## Overview
YouTube Download Helper is a Windows-first desktop app that makes `yt-dlp` usable for non-technical users. It replaces command-line usage with a minimal GUI for downloading public, premium, and member-only YouTube content using the user’s local logged-in browser profile.

## Problem
- `yt-dlp` is powerful but not accessible to non-technical users.
- Premium and member-only videos often require browser cookies, which is difficult to manage manually.
- Users need a simple way to avoid redownloading videos they already saved.

## Goals
- Make YouTube downloads possible in a few clicks.
- Support authenticated downloads from local Chrome and Edge profiles.
- Support both single videos and playlists.
- Prevent duplicate downloads with a persistent archive.
- Ship as a portable Windows app with no installer.

## Non-Goals
- macOS or Linux support in v1.
- Advanced `yt-dlp` option editing.
- Cookie export, sync, or sharing between machines.
- Channel-wide batch management beyond playlist support.

## Target Users
- Primary: non-technical users who want a simple download workflow.
- Secondary: technical users who want a lightweight GUI wrapper instead of repeated CLI commands.

## User Stories
- As a user, I can paste a YouTube URL and click Download.
- As a user, I can choose a simple format preset without learning codecs.
- As a user, I can use my local browser session to access premium/member content.
- As a user, I can download playlists into an organized folder structure.
- As a user, I can rerun a playlist later and skip videos I already downloaded.

## Functional Requirements
- Provide fields for URL, preset, browser, and browser profile.
- Support presets:
  - Best Video
  - Audio MP3
  - Audio M4A
- Discover Chrome and Edge profiles on the local machine.
- Use browser cookies through `yt-dlp` cookies-from-browser integration.
- Save downloads to `%USERPROFILE%\Downloads\YT-DLP Helper`.
- Save playlist items under a playlist subfolder.
- Maintain a persistent `download-archive.txt`.
- Persist last-used browser, profile, and preset in `settings.json`.
- Show status updates, progress, and an activity log.
- Provide a button to open the downloads folder.

## UX Requirements
- Keep the interface single-window and beginner-friendly.
- Avoid exposing raw `yt-dlp` flags in v1.
- Show clear errors for invalid URLs, missing profiles, and authentication failures.
- Prefer explicit, user-readable status messages over technical stack traces.

## Technical Requirements
- Platform: Windows only.
- Runtime: Python 3.13+, Tkinter GUI.
- Downloader: `yt-dlp`.
- Media processing: bundled `ffmpeg` and `ffprobe`.
- Packaging: PyInstaller portable bundle.

## Data & Storage
- App data location: `%LOCALAPPDATA%\YT-DLP Helper\`
- Files:
  - `settings.json`
  - `download-archive.txt`

## Success Criteria
- A non-technical user can complete a download without using the command line.
- Member-only or premium content downloads successfully when the selected local profile is entitled.
- Playlist reruns skip already downloaded items using the archive.
- The app can be distributed as a zipped portable folder and run without installation.

## Known Risks
- Browser cookie extraction may fail if the selected profile is locked, not logged in, or lacks entitlement.
- Upstream site changes may require `yt-dlp` updates.
- Windows packaging increases bundle size because Python, Tk, and `yt-dlp` dependencies are included.
