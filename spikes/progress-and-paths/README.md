# Spike #33: yt-dlp progress-template JSON and non-ASCII download paths

Throwaway harness, not part of the solution. Results go in `docs/spikes/progress-and-paths.md`, and the captured
samples in `docs/spikes/samples/`.

`tools/`, `work/` and `out/` are git-ignored.

## 1. Tools

Download into `tools\dl\` and verify the published checksums:

```bash
gh release download 2026.10.26 -R yt-dlp/yt-dlp -p yt-dlp.exe -p SHA2-256SUMS
gh release download v2.9.8 -R denoland/deno -p 'deno-x86_64-pc-windows-msvc.zip*'
gh release download latest -R yt-dlp/FFmpeg-Builds -p ffmpeg-master-latest-win64-gpl.zip -p checksums.sha256
grep ' yt-dlp.exe$' SHA2-256SUMS | sha256sum -c
grep 'win64-gpl.zip$' checksums.sha256 | sha256sum -c
```

Then extract to `tools\yt-dlp\yt-dlp.exe`, `tools\deno\deno.exe` and `tools\ffmpeg\ffmpeg-master-latest-win64-gpl\bin\`.
On its first run, `run.ps1` copies them into the non-ASCII data folder under `work\`.

## 2. Cases

```powershell
$zoo = 'https://www.youtube.com/watch?v=jNQXAC9IVRw'; $ch = 'https://www.youtube.com/watch?v=b1Fo_M_tj6w'
$hls = 'bv*[protocol=m3u8_native][height<=360]+ba[protocol=m3u8_native]'
./run.ps1 -Case merge -Url $zoo -UnicodeTitle
./run.ps1 -Case audio-mp3 -Url $zoo -UnicodeTitle -Extra '-f','bestaudio','-x','--audio-format','mp3'
./run.ps1 -Case hls -Url $ch -UnicodeTitle -Extra '-f',$hls
./run.ps1 -Case hls-delta -Url $ch -UnicodeTitle -ProgressDelta 0.25 -Extra '-f',$hls
./run.ps1 -Case enc-none -EncodingMode none -UnicodeTitle -NoEmbed -Url $zoo -Extra '-f','139','--print','after_move:RAW %(filepath)s'
./analyze.ps1 merge
```

Each run writes `out\<case>\{args.json,stdout.bin,stderr.bin,exit.txt}`. It clears that case's destination
folder first, so an existing file doesn't short-circuit the download (that is the `already-exists` sample).
