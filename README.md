# ChatRenderCLI

<div align="center">
  <h3>High-Performance Standalone Twitch Chat Video Renderer</h3>
  <p>Render Twitch chat logs into smooth, transparent, or solid video overlays with full support for animated emotes, Giphy GIFs, emojis, and 60+ FPS framerates.</p>
</div>

---

## Overview

**ChatRenderCLI** is a specialized, hardware-accelerated command-line tool engineered to render Twitch chat logs (`.json` or `.json.gz`) into video files (`.mp4`, `.webm`, `.mov`, etc.). 

Built on .NET 10, SkiaSharp, and FFmpeg, it features an optimized frame generation pipeline capable of 60 FPS and 120 FPS rendering, zero-allocation frame repeat tracking, dirty-region animation composition, planar YUV conversion, SIMD alpha processing, and embedded Giphy GIF integration with strict memory budgeting.

Whether you need a **transparent WebM video** for editing chat overlays directly onto VODs in video editors (Wondershare Filmora, Adobe Premiere Pro, DaVinci Resolve), or a solid MP4 for archiving and streaming, ChatRenderCLI renders at maximum efficiency without unnecessary CPU overhead.

---

## Key Features

- **Full Emote & Badge Support**:
  - First-party Twitch emotes and badges (subscriber, moderator, VIP, broadcaster, predictions, etc.).
  - Third-party animated and static emotes: **7TV** (including unlisted), **BetterTTV (BTTV)**, and **FrankerFaceZ (FFZ)**.
  - Native color emoji rendering via **Google Noto Color Emoji** or **Twitter Twemoji**.
- **Giphy GIF Integration**:
  - Automatically renders bracketed Giphy GIFs posted in chat (e.g., `[Horse Stare GIF by Jan Metternich]`).
  - Fetches and caches resolutions locally, or reads pre-embedded GIF data from the chat JSON.
  - Configurable maximum height percent (`--gif-max-height-percent`) and LRU frame memory budget (`--image-cache-mb`).
- **High-Performance 60 FPS & 120 FPS Engine**:
  - **Parallel Timeline Partitioning (`-j` / `--jobs`)**: Automatically partitions the timeline into concurrent encoding segments across multiple FFmpeg workers and stitches them losslessly via FFmpeg's stream-copy concat demuxer (`-f concat -c copy`), overcoming single-encoder VP9/ProRes CPU bottlenecks and achieving 2x–3x faster renders on multi-core CPUs.
  - **Zero-Allocation Frame Repeat Tracking**: Chat idling between scroll and animation ticks bypasses buffer copies and increments frame counters.
  - **Incremental Scroll Shifting**: When chat advances, existing pixels are shifted upward in memory, rasterizing only new comments.
  - **Dirty-Rect Emote Patching**: Only active bounding boxes of animated emotes that stepped forward on the current tick are recomposed.
  - **O(1) Next-Transition Short-Circuiting**: Instantly skips emote scans when no emote frames are scheduled to change on the current tick.
  - **Hardware-Direct Planar YUV Pipeline**: Directly produces planar `yuv420p` or `yuva420p` streams with 64-bit uniform block acceleration, cutting rawvideo pipe bandwidth by 37.5%–62.5% and eliminating redundant FFmpeg `swscale` per-tick conversions.
  - **SIMD Mask Extraction**: SSE2/SSSE3/AVX2 and ARM64 AdvSimd vectorization for instant alpha-mask extraction.
- **Transparent Video & Alpha Channel Support**:
  - Native **WebM VP9 Alpha** (`-pix_fmt yuva420p` via `libvpx-vp9`) with automatic multithreading (`-row-mt 1`, `-cpu-used 8`, dynamic GOP scaling, and multi-process segment encoding).
  - Apple **ProRes 4444** (`prores_ks` with `yuva444p10le`) and QuickTime Animation (`qtrle`).
  - Dual-output **Alpha Mask Generation** (`--generate-mask`) for standard H.264 editors that don't support transparent video tracks.
- **Built-in Typography & Font Mapping**:
  - Embedded high-legibility **Inter** typeface built directly into the binary.
  - Smart font fallback: Passing `--font Inter` automatically uses the embedded Inter typeface unless installed natively on the host OS.
- **Large Archive Streaming**:
  - Stream-based JSON deserialization handles multi-gigabyte (`>2 GB`) chat logs and compressed `.json.gz` archives without out-of-memory errors.
- **Zero External Dependencies**:
  - Downloadable as a single, self-contained Windows executable (`ChatRenderCLI.exe`). Does not require .NET installed on the host machine.
  - Built-in standalone FFmpeg downloader (`ChatRenderCLI.exe ffmpeg -d`).

---

## Quickstart

### 1. Download or Build
Download `ChatRenderCLI.exe` from the Releases page, or build it locally using the provided one-click scripts:
- **Windows Command Prompt**: `build-win-x64.bat`
- **Windows PowerShell**: `./build-win-x64.ps1`

### 2. Ensure FFmpeg is Available
ChatRenderCLI uses FFmpeg to encode the video stream. If FFmpeg is not already on your system PATH:
```cmd
ChatRenderCLI.exe ffmpeg -d
```
This downloads a portable `ffmpeg.exe` binary into the working directory automatically.

### 3. Render a Chat File
```cmd
ChatRenderCLI.exe -i chat.json -o chat.mp4
```

> **Tip**: You can run ChatRenderCLI either with the verb (`chatrender`) or without it. When omitted, `chatrender` is assumed automatically.

---

## Common Workflows & Examples

### 1. Transparent WebM Video for Video Editing (Filmora, Premiere, DaVinci)
Renders a 60 FPS transparent video overlay where the background is completely transparent. Perfect for overlaying directly onto gameplay/VOD tracks:
```cmd
ChatRenderCLI.exe -i chat.json -o chat.webm -w 420 -h 1080 --framerate 60 --background-color "#00000000" --output-args "-c:v libvpx-vp9 -crf 18 -b:v 0 -pix_fmt yuva420p \"{save_path}\""
```
*Note: ChatRenderCLI automatically detects `libvpx-vp9` with `yuva420p` and optimizes threading (`-row-mt 1`, `-cpu-used 4`), frame lookahead, pipe buffer size, and GOP length (`-g <fps*8>`) for optimal performance and timeline scrubbing.*

### 2. High-Performance 60 FPS Solid MP4 (H.264)
Renders a smooth 60 FPS chat video with an outline around messages:
```cmd
ChatRenderCLI.exe -i chat.json -o chat.mp4 -w 420 -h 1080 --framerate 60 --update-rate 0.2 --outline
```

### 3. Transparent Chat with Separate Alpha Mask File
For video editors that do not import WebM alpha channels, generate an H.264 chat video and a synchronized grayscale mask video (`chat_mask.mp4`):
```cmd
ChatRenderCLI.exe -i chat.json -o chat.mp4 -w 400 -h 900 --generate-mask
```

### 4. Custom Styling (Dark Theme, Alternating Rows, Custom Font Size)
```cmd
ChatRenderCLI.exe -i chat.json -o chat.mp4 -w 450 -h 1080 --background-color "#121214" --alt-background-color "#18181b" --alternate-backgrounds --font "Inter" --font-size 15 --framerate 60
```

### 5. Filter Moderation (Ignore Bots, Banned Words, Specific Badges)
```cmd
ChatRenderCLI.exe -i chat.json -o chat.mp4 --ignore-users "Nightbot,StreamElements" --ban-words "spambot,freefollowers" --badge-filter 6
```
*(Badge filter `6` hides Broadcaster and Moderator badges; see [CLI documentation](TwitchDownloaderCLI/README.md#badge-filtering) for filter bitmasks).*

### 6. Completely Offline Render
Render using only the emotes, badges, and GIFs already embedded in the JSON archive:
```cmd
ChatRenderCLI.exe -i chat.json -o chat.mp4 --offline
```

---

## Command-Line Interface Reference

ChatRenderCLI supports three execution modes:
- **`chatrender`** (default): Renders a chat JSON or compressed archive to video.
- **`ffmpeg`**: Downloads or manages a standalone FFmpeg executable.
- **`cache`**: Manages and clears cached emotes, badges, and Giphy thumbnails.

For the complete list of options, flags, scaling arguments, and technical details:
👉 **[Read the Full CLI Documentation](TwitchDownloaderCLI/README.md)**

---

## Platform Support & Prerequisites

### Windows
- Windows 10/11 (x64 / ARM64).
- FFmpeg on PATH or downloaded with `ChatRenderCLI.exe ffmpeg -d`.

### Linux
- Ensure `fontconfig` and `libfontconfig1` are installed:
  ```bash
  sudo apt-get install fontconfig libfontconfig1
  ```
- If rendering non-Latin alphabets (Arabic, Japanese, Thai, etc.), install comprehensive font packages such as [Google Noto Fonts](https://fonts.google.com/noto).

### macOS
- Compatible with macOS 12+ (x64 and Apple Silicon arm64).
- Install FFmpeg via Homebrew:
  ```bash
  brew install ffmpeg
  ```

---

## Building From Source

### Requirements
- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (`10.0.100` or newer)

### Build Commands
To produce a self-contained single-file executable for Windows x64:
```bash
dotnet publish TwitchDownloaderCLI/TwitchDownloaderCLI.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=Embedded \
  -o ./publish
```

Or build for Linux x64:
```bash
dotnet publish TwitchDownloaderCLI/TwitchDownloaderCLI.csproj \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -o ./publish
```

To run all unit tests:
```bash
dotnet test ChatRenderCLI.sln
```

Detailed compilation instructions and CI workflow details are available in [BUILD_GUIDE.md](BUILD_GUIDE.md).

---

## Third-Party Credits & Licenses

- **SkiaSharp & HarfBuzzSharp** (© Microsoft Corporation, Google LLC) — High-performance 2D rasterization and complex text layout.
- **FFmpeg** (© The FFmpeg developers) — Video muxing, scaling, and codec encoding.
- **Noto Color Emoji** (© Google LLC and contributors) — High-quality color emoji font.
- **Twemoji** (© Twitter, Inc. and contributors) — Vector emoji assets.
- **Inter Font Family** (© Rasmus Andersson) — Modern UI typeface bundled directly into the executable.
- **Xabe.FFmpeg.Downloader** (© Tomasz Żmuda) — Automated runtime FFmpeg retrieval.

Full license texts are available in [LICENSE.txt](LICENSE.txt) and `TwitchDownloaderCore/Resources/THIRD-PARTY-LICENSES.txt`.

---

## Disclaimer
TwitchDownloader / ChatRenderCLI is an independent open-source project and is not affiliated with, endorsed by, or associated with Twitch Interactive, Inc., Amazon.com, or GIPHY.
