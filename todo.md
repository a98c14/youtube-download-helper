# to-do

## features
- [x] Double clicking a file on file progress viewer should launch the file if its completed
- [x] We need to improve file naming and downloaded content organization. I want downloaded videos to be put inside a folder based on which channel was downloaded from. This feature should be enabled based on a setting in our configuration. For example, if the channel is "ChannelA" and our download folder is "Downloads", any video I download from that channel should be put inside "Downloads/ChannelA/". Also if that video is part of a playlist then that playlist should also be another folder inside the channel folder as such "Downloads/ChannelA/{PlaylistName}/". 
- [x] When you click "Continue" it shouldn't launch an external command line every time. Also we should cache the system settings for existing session and don't do the checks everytime user tries to download a video.
- [x] Clicking "Add" should automatically start the download process. It shouldn't sit idle on queue. And it shouldn't be named "Add" it should be named "Download" instead.

## minor issues
- [] Clicking "Save" on settings should close the settings screen.
- [] Add missing translations for file progress filter (all, ongoing etc.).
- [x] Update yt-dlp success message should not say ".. restart ..", app doesn't have to be restarted since it's side car program.
- [x] We shouldn't prefix single videos with 'default#' by default.

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
