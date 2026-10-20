# ChatRenderGUI (`TwitchDownloaderWPF`)

**ChatRenderGUI** (`ChatRenderGUI.exe`) is a dedicated, fast-launching Windows WPF desktop frontend for the optimized `TwitchDownloaderCore` chat rendering engine.

It preserves the full TwitchDownloader **Chat Render** visual configurator and real-time SkiaSharp preview while stripping away non-render subsystems (VOD/Clip downloaders, chat downloaders/updaters, queue dialogs, and theme/localization bloat) and exposing all new high-performance rendering capabilities.

---

## Key Features & Additions Over Upstream TwitchDownloaderWPF

1. **Shared 10x–20x Faster `TwitchDownloaderCore` Pipeline**:
   - Because `ChatRenderGUI.exe` calls directly into the upgraded `ChatRenderer` in `TwitchDownloaderCore`, GUI renders automatically benefit from **Parallel Timeline Partitioning**, **Direct Planar YUV (`yuv420p` / `yuva420p`) Pipes**, **Pre-Converted Planar YUV Emote Blitting (`EmoteYuvFrame`)**, **O(1) Next-Transition Short-Circuiting**, **Incremental Scroll Shifting**, **On-Demand Emoji Decoding**, and **Giphy GIF Rendering**.
2. **Parallel Segments (`0 = Auto`) Control**:
   - Located in the **Encoding** tab (`numParallelJobs`), allowing you to choose how many concurrent FFmpeg encoding segments (`0` = Auto-scale by CPU core count & duration, `1` = Single-stream, `2`–`16` = Explicit parallel segments) are used and stitched losslessly via `ffmpeg -f concat -c copy`.
3. **Optimized VP9 Alpha Preset Out of the Box**:
   - Selecting **WEBM** -> **VP9** automatically uses `-deadline realtime -cpu-used 8 -row-mt 1 -lag-in-frames 0 -tile-columns 2 -tile-rows 1` along with the engine's automatic `yuva420p` planar YUV pipe and dynamic GOP scaling—matching the CLI's maximum VP9 encoding speed.
4. **Direct Multi-File Batch Rendering (No Queue Window Needed)**:
   - Click **Browse** and select **multiple** `.json` or `.json.gz` files at once.
   - When multiple files are loaded, clicking **Render** prompts you once for an **Output Folder** (`OpenFolderDialog`) and sequentially renders each chat file (`<input_name>.<ext>`) using your configured settings, updating progress and supporting clean cancellation (automatically terminating child FFmpeg processes and cleaning up `.chatrender` temp files) at any time.
5. **Partial Timeline Rendering**:
   - Use the split **Render** button dropdown -> **Partial Render** to render a specific start/end timestamp window (`HH:MM:SS`) of a chat archive.
6. **Real-Time SkiaSharp Live Preview**:
   - The right-hand **LIVE PREVIEW** panel renders a live SkiaSharp canvas preview (including a transparency checkerboard for alpha channels) that updates immediately whenever you change font family, font size, colors, outlines, timestamps, alternating backgrounds, avatars, badges, or layout scaling.

---

## Configuration Tabs Overview

- **General**: Canvas width (`Width`), height (`Height`), font family (`Font`), font size (`Font Size`), base/alternate/message/highlight colors, timestamps, outlines, and subscription notices.
- **Rendering**: Toggle **BTTV**, **FFZ**, **7TV**, **Giphy GIFs**, **Unlisted 7TV Emotes**, **Avatars**, **Chat Badges**, **Emoji Vendor** (`Google Noto Color`, `Twitter Twemoji`, or `None`), **Offline Mode**, **Dispersion**, **GIF Max Height (%)** (`5`–`100%`), and **Image Cache (MB)** (`0 = Unlimited`, up to `16384` MB). Filter specific badges via the **Filter Chat Badges** checklist or hide/highlight specific users and banned words.
- **Scaling**: Fine-grained multipliers (`0.1x` – `4.0x`) for Emotes, Badges, Emojis, Avatars, Vertical Spacing, Side Padding, Section Height, Word Space, Emote Space, Accent Stroke, Accent Indent, and Username Scale.
- **Encoding**: Container format (`MP4`, `MOV`, `WEBM`, `MKV`), Video Codec (`H264`, `H264 NVIDIA`, `H265`, `H265 NVIDIA`, `VP8`, `VP9`, `ProRes 4444`, `RLE`), Framerate (`FPS`), Update Rate, **Parallel Segments (`0 = Auto`)**, Alpha Mask generation (`Generate Mask`), Sharpening, and custom FFmpeg input/output arguments.

---

## Global Settings (`Settings` Button)

Clicking **Settings** in the top-right opens the streamlined global preferences dialog:
- **Temp Cache Folder**: Browse or clear the temporary cache folder storing downloaded emotes, badges, and Giphy resolutions.
- **Log Verbosity**: Configure which log categories (`Status`, `Verbose`, `Info`, `Warning`, `Error`, `Ffmpeg`) appear in the status log.

---

## Building `ChatRenderGUI.exe`

Run `build-win-x64.bat` or `./build-win-x64.ps1` from the repository root, or publish directly via the .NET 10 SDK:

```bash
dotnet publish TwitchDownloaderWPF/TwitchDownloaderWPF.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:IncludeAllContentForSelfExtract=true \
  -o ./dist/win-x64
```
This outputs a standalone single-file Windows desktop executable at `dist/win-x64/ChatRenderGUI.exe`.
