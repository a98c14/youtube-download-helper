# to-do

## bugs
- [x] When installed on another computer, yt-dlp sometimes gives the following error when downloading videos (tested with members only video with a correct cookie). Error doesn't occur on the development machine. Fix.
```
[2026-05-24 22:01:37] [youtube] iLE6zsfH07o: Downloading webpage
[2026-05-24 22:01:38] [youtube] iLE6zsfH07o: Downloading tv downgraded player API JSON
[2026-05-24 22:01:38] [youtube] iLE6zsfH07o: Downloading web creator client config
[2026-05-24 22:01:39] [youtube] iLE6zsfH07o: Downloading player c2f7551f-main
[2026-05-24 22:01:39] [youtube] iLE6zsfH07o: Downloading web creator player API JSON
[2026-05-24 22:01:39] ERROR: [youtube] iLE6zsfH07o: Requested format is not available. Use --list-formats for a list of available formats
```

## features
- [ ] Create an issue button. Users should be able to create bug reports when they encounter an error. The button could either be on context menu at the top and also on error popups. Issue should be created on github with activity logs and relevant context attached.
- [ ] Double clicking a file on file progress viewer should launch the file if its completed
- [ ] Add missing translations for file progress filter (all, ongoing etc.).
- [] Contact support button under 'Help' menu. It should mail the latest activity log and have a little message box for custom message by the user. Mail should be sent to 'selimyesilkaya@gmail.com'. It can use the user's own mail account or maybe it can send it via an online service I am not sure.

## minor issues
- [x] Update yt-dlp success message should not say ".. restart ..", app doesn't have to be restarted since it's side car program.
- [] We shouldn't prefix single videos with 'default#' by default.
- 
