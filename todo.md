# to-do

codex resume 019edc5d-5a38-7951-94d4-e81b3b4c0123

## issues

## features
- [] Ability to factory reset the app. It should delete all the sqlite data, yt-dlp archive data and other logs. It should be like starting fresh. It should warn the user before doing so. It shouldn't delete any downloaded videos. Just the logs.
- [] We shouldn't rely on yt-dlp playlist download feature. We should download playlist videos separately and track them separately. We should also not rely on yt-dlp download archive instead we use our own sqlite db for tracking those.
- [] Channel tracking feature. Very similar to playlist tracking but it tracks the channel. It shouldn't download all the videos but there should be a cut off date that can be set by user (e.g since 2025-01-01). Also if a video is tracked by playlist tracker it shouldn't be downloaded again by the channel tracker. They should reside in the same window and it should be named "Auto Tracker" (naming open to suggestions). It accepts both playlist and channels.
