# ChatRenderCLI & ChatRenderGUI — Build & Compilation Guide

This repository contains the standalone, hardware-accelerated Twitch Chat Renderer extracted and enhanced from TwitchDownloader (`u/pgeneva/giphy_emotes_v1`), providing both:

1. **`ChatRenderCLI.exe`** (`TwitchDownloaderCLI`) — A **100% drop-in compatible** command-line replacement for `TwitchDownloaderCLI.exe chatrender`, `ffmpeg`, and `cache`.
2. **`ChatRenderGUI.exe`** (`TwitchDownloaderWPF`) — A streamlined Windows WPF desktop GUI focused exclusively on Chat Rendering, live SkiaSharp previewing, and direct multi-file batch rendering.

Non-render subsystems (`videodownload`, `clipdownload`, `chatdownload`, `chatupdate`, `info`, `mergets`, `update`, task queues, and theme/localization bloat) have been stripped out, while the core rendering engine (`TwitchDownloaderCore`) has been upgraded to deliver **10x–20x+ faster rendering speeds**.

---

## Drop-In TwitchDownloader Compatibility & New Capabilities

### 100% Drop-In Compatibility
- **CLI Drop-In**: Accepts all standard `TwitchDownloaderCLI chatrender` arguments, legacy `-m ChatRender` flags, or direct option invocation without a verb (`ChatRenderCLI.exe -i chat.json -o chat.mp4 ...`).
- **JSON Archive Compatibility**: Reads all `.json` and `.json.gz` chat logs exported by any version of TwitchDownloader.

### Major Additions Over Upstream TwitchDownloader
- **Parallel Timeline Segment Partitioning (`-j` / `--jobs` & GUI Parallel Segments)**:
  - Partitions the render timeline across `N` concurrent worker instances (`RenderWorkerState`) and independent FFmpeg processes, then losslessly concatenates the segments via `ffmpeg -f concat -c copy`.
- **Direct Planar YUV Pipeline (`yuv420p` / `yuva420p`)**:
  - Converts BGRA frames to planar `yuv420p` or `yuva420p` directly in C# using precomputed BT.601 integer LUTs and 64-bit uniform block fast-paths, cutting pipe bandwidth by **37.5%–62.5%** and eliminating FFmpeg's single-threaded `swscale` filter.
- **Pre-Converted Planar YUV Emote Cache (`EmoteYuvFrame`) & Dirty-Rect Blitting**:
  - Pre-converts animated emote/GIF frames into planar YUV + Alpha once on first draw and blits advancing emote rectangles directly into the YUV buffer on animation ticks without touching BGRA.
- **O(1) Next-Transition Short-Circuiting & Incremental Scroll Shifting**:
  - Skips high-FPS ticks where no emote frame advances in `< 0.001 ms`, and shifts existing canvas pixels upward in-place when new comments appear.
- **Giphy GIF Rendering (`--giphy`, `--gif-max-height-percent`, `--image-cache-mb`)**:
  - Detects bracketed Giphy titles in chat comments (e.g. `[Horse Stare GIF by Jan Metternich]`), resolves them 8-at-a-time or reads embedded GIF data, and manages decoded frame RAM with an off-screen LRU memory budget (`--image-cache-mb`, default `256` MB).
- **Smart `"Inter"` Font Resolution**:
  - Resolves `--font Inter` to the bundled `Inter Embedded` typeface when `"Inter"` is not installed on the host OS.
- **>2 GB Streaming Chat JSON Deserialization**:
  - Automatically streams multi-gigabyte `.json` and `.json.gz` archives.

---

## Prerequisites (Local Compilation)

- **.NET 10.0 SDK** (`10.0.100` or newer)
  - Download from: https://dotnet.microsoft.com/download/dotnet/10.0
  - Verify installation in a terminal with: `dotnet --version`

*(Note: Both `ChatRenderCLI.exe` and `ChatRenderGUI.exe` are **self-contained** — end users running the compiled `.exe` files do **not** need .NET installed on their machines.)*

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

Both scripts build and publish standalone, self-contained single-file executables into `./dist/win-x64/`:
```text
./dist/win-x64/ChatRenderCLI.exe
./dist/win-x64/ChatRenderGUI.exe
```

---

## Option 2: Direct `.NET CLI` Commands

### 1. Build `ChatRenderCLI.exe` (Command-Line Interface)
```bash
dotnet publish TwitchDownloaderCLI/TwitchDownloaderCLI.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=none \
  -p:DebugSymbols=false \
  -o ./dist/win-x64
```

### 2. Build `ChatRenderGUI.exe` (Windows WPF Desktop App)
```bash
dotnet publish TwitchDownloaderWPF/TwitchDownloaderWPF.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:IncludeAllContentForSelfExtract=true \
  -p:DebugType=none \
  -p:DebugSymbols=false \
  -o ./dist/win-x64
```

### 3. Run Unit Tests (`GiphyResolverTests`, `ImageMemoryBudgetTests`, & CLI/Core Suite)
```bash
dotnet test TwitchDownloaderCore.Tests/TwitchDownloaderCore.Tests.csproj
dotnet test TwitchDownloaderCLI.Tests/TwitchDownloaderCLI.Tests.csproj
```

---

## Option 3: Automated GitHub Actions Build (No Local SDK Needed)

1. Push this repository to GitHub.
2. Open the **Actions** tab in your GitHub repository.
3. Select the **Build ChatRenderCLI & ChatRenderGUI** workflow (`.github/workflows/build-chatrender.yml`), which runs automatically on every `push` or manually via **Run workflow** (`workflow_dispatch`).
4. Download the **`ChatRender-win-x64`** artifact containing both `ChatRenderCLI.exe` and `ChatRenderGUI.exe`.

---

## Usage Examples

### 1. Drop-In Replacement Mode (with `chatrender` verb, Parallel Jobs & Giphy options)
```powershell
ChatRenderCLI.exe chatrender -i chat.json -o chat.mp4 -h 1080 -w 420 --framerate 60 --update-rate 0.2 -j 0 --giphy true --gif-max-height-percent 50 --image-cache-mb 256
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
ChatRenderCLI.exe -i chat.json -o chat.webm -w 420 -h 1080 --framerate 60 -j 4 --background-color "#00000000" --output-args "-c:v libvpx-vp9 -crf 18 -b:v 0 -pix_fmt yuva420p \"{save_path}\""
```

### 5. Manage Standalone FFmpeg & Cache
```powershell
ChatRenderCLI.exe ffmpeg -d
ChatRenderCLI.exe cache -c
ChatRenderCLI.exe cache --force-clear
```
