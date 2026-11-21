# Jellyfin StreamReady

<img src="images/logo.png" alt="StreamReady" width="96" height="96">

A Jellyfin **10.11** plugin that finds movies and episodes that are too large or incompatible for Direct Play, then pre-encodes them into a stream-friendly file.

Typical problem file: MKV + HEVC Main 10 + Dolby Vision + TrueHD Atmos. Jellyfin live-transcodes that because the container, audio codec, and video range are unsupported. StreamReady does the same work ahead of time.

## Modes

- **Manual (default)** — Scan builds a **Needs Encoding** list. Encode one file, or select several / all visible and queue them.
- **Auto Direct Pre-Transcode** — After a scan, matching files are queued automatically. The worker still encodes one at a time and skips files that are playing.

Scanning never silently rewrites your library. Auto is off until you turn it on, and no libraries are selected until you save them.

## Encode planning

- Container-only and codecs already fit the destination → remux (`-c copy`)
- Audio/container only, video already compatible → copy video, encode audio
- Video codec, HDR/Dolby Vision, resolution, or size → full encode

Replacement policy is a setting: backup then replace (recommended), replace in place, or write a `.streamready` sidecar.

If MP4 cannot preserve the selected subtitle tracks, StreamReady tries MKV when your allowed containers include MKV or the container list is unrestricted. Otherwise, the job stops before encoding. Output verification is mandatory before publication.

### Series folders

Choose **Series folders** in Needs Encoding to expand or collapse flagged episodes by
series. **Encode whole series** queues all flagged episodes in that series, including
episodes outside the current search, and skips jobs that are already waiting or running.

### Reviewing replacements

The Replacements tab shows original and encoded sizes and plays short browser-compatible
samples from either version at a selected time. With Backup policy, newly recorded originals
remain available until you restore them or explicitly delete the original backup.
Older records may still follow their configured retention policy.

### Upgrading and recovery

After upgrading, test Manual mode on a disposable library, first with Sidecar and then with Backup and Restore. Only enable Auto after confirming the encoded item plays and Jellyfin sees the new path after a container change.

StreamReady preserves the legacy queue snapshot as `jobs.json.legacy`. The dashboard reports when the primary and previous snapshots are unavailable or a media operation requires recovery. For an interrupted operation, preserve the journal and all paths it lists until you have identified the playable copy and reconciled the library. Backups created by older versions lack the fingerprints required for dashboard restore and can be restored manually.

The plugin's interval controls its recurring scans. A Jellyfin scheduled-task trigger already configured on your server remains independent and can still invoke a scan. Encode windows use server-local time, with an overnight window counted on the day it starts.

## Install

Requires **Jellyfin Server 10.11.x**.

### From the custom catalog (after a GitHub release)

1. Dashboard → Plugins → Repositories → Add
2. Name: `StreamReady`
3. URL:

```text
https://raw.githubusercontent.com/ThuGie/jellyfin-plugin-streamready/main/manifest.json
```

4. Catalog → install **StreamReady** → restart Jellyfin

### Manual

1. Download the release zip
2. Extract `Jellyfin.Plugin.StreamReady.dll` into the server plugins folder (for example `%ProgramData%\Jellyfin\Server\plugins\StreamReady` on Windows)
3. Restart Jellyfin

## Settings

Dashboard → StreamReady (also in the plugin menu):

| Tab | Purpose |
| --- | --- |
| Overview | Manual vs Auto, FFmpeg status, scan / pause / resume |
| Needs Encoding | Review list, encode one or selected |
| Queue | Jobs that were actually queued |
| Compatibility | Allowed containers/codecs/HDR, size and bitrate caps |
| Encoding | Presets, CRF, hardware accel, tone-map |
| Libraries | Which movie/TV libraries to scan |
| Replacement | Backup / replace / sidecar |
| Schedule | Scan interval, pause during playback |
| Docs | Short help |

Default output preset is **Balanced**: MP4 + H.264 High + AAC 5.1 + SDR tone-map at a high CRF.

## Build

```bash
dotnet publish Jellyfin.Plugin.StreamReady/Jellyfin.Plugin.StreamReady.csproj -c Release
```

Run the regression suite with FFmpeg and FFprobe on the path:

```bash
dotnet run --project tests/StreamReady.Regression -c Release -- --ffmpeg
```

Copy the published `Jellyfin.Plugin.StreamReady.dll` (and `thumb.png`) into the Jellyfin plugins directory.

GitHub Actions builds on every push and pull request. Pushing a version tag publishes the release:

```bash
git tag v1.0.7.0
git push origin v1.0.7.0
```

The tag must match `AssemblyVersion` in the csproj (for example `v1.0.7.0`). CI then builds the zip, creates the GitHub Release, and writes the MD5 checksum into `manifest.json`, which Jellyfin uses to verify catalog installs. You can also Draft a GitHub Release with that tag; publishing it creates the same tag and runs the same job.

The catalog icon is `images/logo.png` at **256×256 PNG** (Jellyfin’s plugin cards display around 80px; 256 stays sharp on HiDPI). The same file is shipped as `thumb.png` inside the plugin zip.

## License

GPL-3.0 (same as Jellyfin; this plugin links the Jellyfin server APIs).
