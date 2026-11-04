# Wrestling Finder for Jellyfin

A Jellyfin plugin that finds wrestling events and downloads them into your library.

1. **Type an event**, however you like: `Elimination Chamber 2025`, `ec 25`, `WM 41`, `rumble 2024`.
   The plugin checks Wikipedia and shows the proper event names it finds. The best match is picked for you, and you can click a different one.
2. **It searches** YouTube, the Internet Archive and Dailymotion for the chosen event.
3. **You see every result** with its thumbnail, source, uploader, **length** and a **direct link**, so you can open the video and check it yourself.
   - Anything shorter than **50 minutes** (you can change this) or titled like a highlight, preview, reaction or review is hidden, so you only see possible full shows.
   - Tick **Show hidden results** to see everything.
4. **Click Download** on the one you want. It's saved as `<Download folder>/<Event Name>/<Event Name>.mkv` with a poster, and Jellyfin scans the library automatically.

## Install

### Option A: plugin repository (easiest, gets updates)
1. Jellyfin → **Dashboard → Plugins → Repositories → +**
2. Name: `Wrestling Finder`
   URL: `https://github.com/Ollyisgreat/jellyfin-wrestling-finder/releases/latest/download/manifest.json`
3. **Catalog** → *Wrestling Finder* → Install, then restart Jellyfin.

### Option B: manual
Download the zip from [Releases](../../releases), unzip it into a `WrestlingFinder` folder inside Jellyfin's `plugins` directory, and restart Jellyfin.

Requires **Jellyfin 10.11.x**.

## Setup
1. Open **Wrestling Finder** from the Jellyfin side menu (or Dashboard → Plugins → Wrestling Finder).
2. Open **Settings**, set a **Download folder** that Jellyfin can write to (e.g. `/media/wrestling`), and save.
3. Add that same folder as a library in Jellyfin (content type *Movies* or *Home Videos*).

**yt-dlp** does the downloading. If it isn't installed on the server, the plugin downloads it automatically into its own data folder. It also works in the official Docker image. Sites change often, so use **Settings → Update yt-dlp** if downloads start failing. Jellyfin's own ffmpeg is used to merge video and audio.

## Settings
| Setting | Default | |
|---|---|---|
| Minimum length | 50 min | Shorter results are hidden as highlights |
| Results per site | 10 | |
| Max quality | 1080p | |
| Sources | YouTube, Internet Archive, Dailymotion | Turn each on or off |
| Scan after download | On | |

## Note
Only download videos you have the right to. Many full-event uploads on these sites are unofficial.

## Build it yourself
```
dotnet publish Jellyfin.Plugin.WrestlingFinder -c Release -o out
```
Pushing to `main` builds the plugin with GitHub Actions and publishes a release with `manifest.json`.
