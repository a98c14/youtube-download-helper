# to-do

## features
- [x] Instead of bundling ffmpeg, yt-dlp etc. Program should be able to download them if necessary.
- [x] Github action for automated releases.
- [x] Instead of 'Update yt-dlp' we should just have a single update button and it should update all the dependencies and the app itself. We should also be able to update the app itself update should use the latest github release we have on the repo.
- [x] Turkish language translations. Also a settings button under 'File' menu that opens a settings panel. Language selection should be in there. It should use 'Turkish' by default but also remember the settings selections by the user.
- [] Ability to download playlist urls as single video (ignoring the playlist). We should have separate button for downloading as playlist.
- [] About screen that tells us about the app, ffmpeg and yt-dlp versions.
- [] Editable video file name format.
- [] Multiple file download support. We should have a table view like in torrent clients with columns like video name, progress percentages (no bar), speed, video add date.
- [] Activity Log should only show the current session's log. Not the whole log file.
- [] Contact support button under 'Help' menu. It should mail the latest activity log and have a little message box for custom message by the user. Mail should be sent to 'selimyesilkaya@gmail.com'. It can use the user's own mail account or maybe it can send it via an online service I am not sure.

## minor issues
- [x] Update yt-dlp success message should not say ".. restart ..", app doesn't have to be restarted since it's side car program.
