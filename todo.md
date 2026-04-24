# to-do

## features
- [x] Instead of bundling ffmpeg, yt-dlp etc. Program should be able to download them if necessary.
- [x] Github action for automated releases.
- [x] Instead of 'Update yt-dlp' we should just have a single update button and it should update all the dependencies and the app itself. We should also be able to update the app itself update should use the latest github release we have on the repo.
- [x] Turkish language translations. Also a settings button under 'File' menu that opens a settings panel. Language selection should be in there. It should use 'Turkish' by default but also remember the settings selections by the user.
- [x] Ability to download playlist urls as single video (ignoring the playlist). We should have separate button for downloading as playlist.
- [x] About screen that tells us about the app and yt-dlp versions.
- [x] Editable video file name format. This should be located in settings screen. Also move the download folder to the settings screen. We can remove the download folder from the main view. Just keep 'Go to download folder' button but move it next to download buttons. 
- [] Refactor worker status pipeline into a deeper module so background tasks report through a testable boundary instead of raw English status strings. https://github.com/a98c14/youtube-download-helper/issues/1
- [] Multiple file download support. We should have a table view like in torrent clients with columns like video name, progress percentages (no bar), speed, video add date instead of the current progress bar, status. Download button should instead add to queue. Initially only one file will be downloaded at a time but this will be configurable. There should be right click context menu for retrying failed download attempts. There should be an option to move up or down inside the queue.
- [] Activity Log should only show the current session's log. Not the whole log file.
- [] Contact support button under 'Help' menu. It should mail the latest activity log and have a little message box for custom message by the user. Mail should be sent to 'selimyesilkaya@gmail.com'. It can use the user's own mail account or maybe it can send it via an online service I am not sure.

## minor issues
- [x] Update yt-dlp success message should not say ".. restart ..", app doesn't have to be restarted since it's side car program.
