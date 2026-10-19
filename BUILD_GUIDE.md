# ChatRenderCLI — Build & Compilation Guide (`u/pgeneva/giphy_emotes_v1`)

This repository contains the standalone command-line extraction of **ChatRenderCLI** (`ChatRenderCLI.exe`) synchronized verbatim with commit `0ec45f97ff64a91990a472d27341c8421b82b23d` of the `u/pgeneva/giphy_emotes_v1` branch.

The WPF GUI (`TwitchDownloaderWPF`) and non-render CLI modes (`videodownload`, `clipdownload`, `chatdownload`, `chatupdate`, `info`, `mergets`, `update`) have been removed, leaving a lightweight, 100% drop-in compatible CLI focused exclusively on:

- `chatrender` (or direct option invocation without a verb)
- `ffmpeg` (`ChatRenderCLI.exe ffmpeg -d` to download standalone `ffmpeg.exe`)
- `cache` (`ChatRenderCLI.exe cache -c` or `--force-clear` to clear emote/badge/Giphy cache)

---

## `u/pgeneva/giphy_emotes_v1` & Performance Capabilities Included

- **High-Performance 60 & 120 FPS Rendering**:
  - **Zero-Allocation Frame Repeat Tracking**: Idling chat ticks bypass frame allocations and increment repeat counters.
  - **Incremental Scroll Shifting & Dirty-Rect Emote Patching**: Only exposed bottom rows and changed emote bounding boxes are re-rendered.
  - **O(1) Next-Transition Short-Circuiting**: Bypasses emote scan loops when no active emotes change frames on the current tick.
  - **Direct Planar YUV Pipeline**: Directly feeds planar `yuva420p` (WebM VP9 alpha) or `yuv420p` (H.264) with 64-bit uniform block acceleration, cutting rawvideo pipe bandwidth by 37.5%–62.5% and bypassing FFmpeg `swscale` per-tick conversions.
  - **Optimized VP9 Alpha Encoding**: Injects `-row-mt 1`, `-cpu-used 4`, dynamic `-g <fps*8>`, and 1024-entry rawvideo queues for seamless editing in Wondershare Filmora 15, Premiere Pro, and DaVinci Resolve.
- **Embedded Inter Font Fallback**:
  - Automatically resolves `--font Inter` to the built-in embedded Inter typeface unless installed natively on the system.
- **Giphy GIF Rendering (`--giphy`, default `true`)**: Detects bracketed Giphy titles in Twitch chat comments (e.g. `[Horse Stare GIF by Jan Metternich]`), resolves them 8-at-a-time against Giphy search or reads embedded GIF data from the input JSON, caches resolutions globally in `giphy/resolved.json.gz`, and renders resolved GIFs full-width on their own line below the username. Unresolved titles remain as verbatim text without false-positive emote substitution.
- **GIF Height Cap (`--gif-max-height-percent`, default `50`)**: Caps the height of a posted GIF as a percentage of the chat height (`100` lets a single GIF fill the chat height).
- **Lazy Emote Frame Decoding & Memory Budget (`--image-cache-mb`, default `256`)**: `TwitchEmote` decodes frames on first draw rather than in the constructor, and `ImageMemoryBudget` evicts the least-recently-drawn off-screen emote/GIF frames once off-screen decoded imagery exceeds `--image-cache-mb`.
- **Streaming Deserialization for >2 GB Chat JSON Archives**: `ChatJson` automatically falls back to stream deserialization when opening >2 GB `.json` or `.json.gz` files with embedded GIFs.

---

## Prerequisites (Local Compilation)

- **.NET 10.0 SDK** (`10.0.100` or newer)
  - Download from: https://dotnet.microsoft.com/download/dotnet/10.0
  - Verify installation in a terminal with: `dotnet --version`

*(Note: The compiled `ChatRenderCLI.exe` is **self-contained** — end users running `ChatRenderCLI.exe` do **not** need .NET installed on their machines.)*

---

## Option 1: One-Click Local Build Scripts (Windows)

From the root of the repository, run either of the included build scripts:

### A. Command Prompt / File Explorer (`build-win-x64.bat`)
Double-click `build-win-x64.bat` in Windows Explorer, or run in Command Prompt:
```cmd
build-win-x64.bat
```

### B. PowerShell (`build-win-x64.ps1`)
Run in PowerShell:
```powershell
./build-win-x64.ps1
```

Both scripts verify that the required embedded emoji archives (`twemoji-17.0.3.zip` and `noto-emoji-2.051.zip`) exist in `TwitchDownloaderCore/Resources/` and produce a self-contained single-file executable at:
```text
./publish/ChatRenderCLI.exe
```

---

## Option 2: Direct `.NET CLI` Commands

### Standard Self-Contained Single-File Build (`./publish/ChatRenderCLI.exe`)
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

### Using the Built-In Publish Profile (Includes Partial IL Trimming)
```bash
dotnet publish TwitchDownloaderCLI -p:PublishProfile=Windows -p:DebugType=Embedded
```
This outputs `ChatRenderCLI.exe` to:
```text
TwitchDownloaderCLI/bin/Release/net10.0/publish/Windows/ChatRenderCLI.exe
```

### Running Unit Tests (Including `GiphyResolverTests` & `ImageMemoryBudgetTests`)
```bash
dotnet test ChatRenderCLI.sln
```

---

## Option 3: Automated GitHub Actions Build (No Local SDK Needed)

1. Push this repository to GitHub.
2. Open the **Actions** tab in your GitHub repository.
3. Select the **Build ChatRenderCLI** workflow (`.github/workflows/build-chatrender.yml`), which runs automatically on every `push` or manually via **Run workflow** (`workflow_dispatch`).
4. Download the **`ChatRenderCLI-win-x64`** artifact containing `ChatRenderCLI.exe`.

---

## Usage Examples

### 1. Drop-In Replacement Mode (with `chatrender` verb & Giphy options)
```powershell
ChatRenderCLI.exe chatrender -i chat.json -o chat.mp4 -h 1080 -w 420 --framerate 60 --update-rate 0.2 --giphy true --gif-max-height-percent 50 --image-cache-mb 256
```

### 2. Direct Utility Mode (verb omitted — `chatrender` is prepended automatically)
```powershell
ChatRenderCLI.exe -i chat.json -o chat.mp4 -h 1080 -w 420 --framerate 60 --giphy true --image-cache-mb 512
```

### 3. Legacy Syntax Mode (automatic `-m` / `--mode` conversion)
```powershell
ChatRenderCLI.exe -m ChatRender -i chat.json -o chat.mp4
```

### 4. Transparent WebM VP9 Alpha (Video Editors / Overlays)
```powershell
ChatRenderCLI.exe -i chat.json -o chat.webm -w 420 -h 1080 --framerate 60 --background-color "#00000000" --output-args "-c:v libvpx-vp9 -crf 18 -b:v 0 -pix_fmt yuva420p \"{save_path}\""
```

### 5. Manage Standalone FFmpeg & Cache
```powershell
ChatRenderCLI.exe ffmpeg -d
ChatRenderCLI.exe cache -c
ChatRenderCLI.exe cache --force-clear
```
