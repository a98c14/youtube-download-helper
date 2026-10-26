# Youtube Download Helper
I want to create a program that lets me download youtube videos using yt-dlp. I want the UI to be native to Windows and have a design similar to qBittorrent or similar clients but instead of torrents we should be queuing youtube videos (or any other kind of videos supported by yt-dlp). It should be very robust and handle failures gracefully, have retry mechanisms etc. One of the most important and key requirement is to ability to download subscription only videos via cookies. I already have an account that have the necessary subscriptions so we need to be able to use the cookies I have.

App should also support automatic updates and yt-dlp updates. I already have a prototype in .archive but it is not that good. It was just there to check if I would use such a program. So don't use that to make decisions, we are creating this from scratch with solid design choices.

## Open Questions
- What would be the ideal way to get the cookies from the user? Is it possible to do some sort of automation or do we need to do manual copy pasting?
