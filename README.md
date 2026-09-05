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

Copy the published `Jellyfin.Plugin.StreamReady.dll` (and `thumb.png`) into the Jellyfin plugins directory.

GitHub Actions builds on every push and pull request. Pushing a version tag publishes the release:

```bash
git tag v1.0.4.0
git push origin v1.0.4.0
```

The tag must match `AssemblyVersion` in the csproj (for example `v1.0.4.0`). CI then builds the zip, creates the GitHub Release, and writes the MD5 checksum into `manifest.json`, which Jellyfin uses to verify catalog installs. You can also Draft a GitHub Release with that tag; publishing it creates the same tag and runs the same job.

The catalog icon is `images/logo.png` at **256×256 PNG** (Jellyfin’s plugin cards display around 80px; 256 stays sharp on HiDPI). The same file is shipped as `thumb.png` inside the plugin zip.

## License

GPL-3.0 (same as Jellyfin; this plugin links the Jellyfin server APIs).
