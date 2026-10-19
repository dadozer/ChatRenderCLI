# ChatRenderCLI & ChatRenderGUI

<div align="center">
  <h3>High-Performance Drop-In Twitch Chat Video Renderer (CLI & Windows GUI)</h3>
  <p>Render Twitch chat logs into smooth, transparent, or solid video overlays <strong>10x–20x faster</strong> than stock TwitchDownloader, with 100% drop-in CLI/JSON compatibility, parallel timeline partitioning, Giphy GIF support, and 60–120+ FPS hardware-direct YUV pipelines.</p>
</div>

---

## Overview

**ChatRender** is a specialized, ultra-fast extraction and architectural overhaul of the TwitchDownloader chat rendering engine, available as both:
- **`ChatRenderCLI.exe`** — A standalone, 100% drop-in compatible command-line renderer for scripts, automated VOD pipelines, and power users.
- **`ChatRenderGUI.exe`** — A streamlined, single-window Windows WPF desktop application focused exclusively on chat rendering, live previewing, and direct multi-file batch rendering.

Built on **.NET 10**, **SkiaSharp**, and **FFmpeg**, the rendering pipeline has been re-engineered from the ground up to eliminate CPU, memory-bandwidth, and encoder bottlenecks—achieving **10x to 20x faster total render speeds** (and up to **30x+ faster** on high-framerate 60/120 FPS transparent WebM VP9 overlays) while maintaining pixel-accurate visual parity with upstream TwitchDownloader.

---

## 100% Drop-In TwitchDownloader Compatibility

`ChatRenderCLI.exe` is designed as a **zero-friction drop-in replacement** for `TwitchDownloaderCLI.exe chatrender`:

1. **Identical CLI Flags & Short Options**: Every standard `TwitchDownloaderCLI chatrender` option (`-i`, `-o`, `-w`, `-h`, `--framerate`, `--update-rate`, `--background-color`, `--outline`, `--bttv`, `--ffz`, `--stv`, `--badge-filter`, `--input-args`, `--output-args`, etc.) works without changing a single character in existing batch files, PowerShell scripts, or third-party integrations.
2. **Three Supported Invocation Syntaxes**:
   - **Standard Verb Syntax**: `ChatRenderCLI.exe chatrender -i chat.json -o chat.mp4 ...`
   - **Direct Utility Syntax (Verb Omitted)**: `ChatRenderCLI.exe -i chat.json -o chat.mp4 ...` *(automatically infers `chatrender` when the first argument is a flag)*
   - **Legacy Syntax**: `ChatRenderCLI.exe -m ChatRender -i chat.json -o chat.mp4 ...`
3. **Full Archive Compatibility**: Reads all `.json` and compressed `.json.gz` chat files produced by any version of TwitchDownloader (including embedded first-party/third-party emotes, badges, cheer bits, and user avatars).
4. **FFmpeg & Cache Subcommands Retained**: `ChatRenderCLI.exe ffmpeg -d` and `ChatRenderCLI.exe cache -c` / `--force-clear` work identically to upstream TwitchDownloaderCLI.

---

## Additions & Enhancements Over Upstream TwitchDownloader

### 1. Massive Rendering & Encoding Speedups (10x–20x+ Faster)
| Optimization Subsystem | Upstream TwitchDownloader | Enhanced ChatRender Engine |
| :--- | :--- | :--- |
| **Multi-Process Encoding (`-j` / `--jobs`)** | Single FFmpeg process; bottlenecked by single-encoder VP9 alpha / ProRes threading limits | **Parallel Timeline Partitioning**: Splits the render timeline into `N` concurrent segments across isolated worker instances and losslessly stitches them via `ffmpeg -f concat -c copy` |
| **Color Conversion & Pipe I/O** | Writes raw 4-byte/pixel `bgra` over `stdin`; forces FFmpeg to run single-threaded `swscale` on every frame | **Direct Planar YUV (`yuv420p` / `yuva420p`)**: Converts frames in C# via precomputed BT.601 LUTs and 64-bit uniform block fast-paths, cutting pipe bandwidth by **37.5%–62.5%** and eliminating `swscale` |
| **Animated Emote Composition** | Re-composites the entire canvas in `BGRA` and re-converts the full frame to YUV on every animation tick | **Pre-Converted Planar YUV Emote Cache (`EmoteYuvFrame`) & Dirty-Rect Patching**: Converts emote/GIF frames to planar YUV+A once on first draw and blits only advancing emote rectangles directly into the YUV buffer |
| **High-FPS Idle & Animation Ticks** | Iterates through all active emotes and copies buffers on every tick | **O(1) Next-Transition Short-Circuiting (`_animNextChangeTickMs`)**: Skips ticks where no emote changes frame in `< 0.001 ms` and uses zero-allocation repeat frame tracking |
| **Chat Scrolling** | Redraws the entire visible comment stack on every scroll step | **Incremental Scroll Shifting**: Shifts existing canvas pixels upward in memory and rasterizes only newly revealed bottom rows |
| **Startup & Emoji Loading** | Decodes all ~3,689 emoji PNGs upfront; runs `O(Emotes × Comments)` regex scans | **On-Demand Emoji Decoding & Single-Pass Span Tokenizer**: Indexes emoji filenames in `< 1 ms` (decoding only used emojis) and tokenizes all comments in a single `O(N)` pass |
| **Lookahead Pre-Rendering** | Sequential main-thread comment layout and frame extraction | **Hardware-Adaptive Lookahead Workers**: Pre-renders upcoming `CommentSection` bitmaps and pre-decodes multi-frame emotes/GIFs on background worker threads |
| **Alpha Mask Generation (`--generate-mask`)** | Scalar byte-by-byte alpha extraction | **SIMD Vectorized Masking**: `AVX-512BW`, `AVX2`, `SSSE3`, and ARM64 `AdvSimd` intrinsics processing up to 64 bytes per instruction |

### 2. New Features & Capabilities
- **Giphy GIF Chat Rendering (`--giphy`, `--gif-max-height-percent`)**:
  - Automatically detects bracketed Giphy GIFs posted in Twitch chat (e.g., `[Horse Stare GIF by Jan Metternich]`).
  - Resolves GIFs 8-at-a-time via Giphy search (cached globally in `giphy/resolved.json.gz`) or reads pre-embedded GIF data from the JSON archive, rendering them full-width on their own line below the username.
  - Configurable maximum height cap (`--gif-max-height-percent`, default `50%` of chat height).
- **Lazy Frame Decoding & LRU Memory Budget (`--image-cache-mb`)**:
  - Decodes emote and GIF frames lazily on first draw rather than upfront at startup.
  - Enforces a configurable off-screen LRU memory budget (`--image-cache-mb`, default `256` MB) that evicts off-screen frames while keeping active visible animations resident in RAM.
- **Multi-Gigabyte (`>2 GB`) Streaming Chat JSON Deserializer**:
  - Automatically uses streaming JSON deserialization for massive `.json` and `.json.gz` archives containing embedded GIFs/emotes without hitting .NET's 2 GB array limit.
- **Smart Embedded `"Inter"` Font Resolution**:
  - Bundles the complete **Inter** font family (`Inter Embedded`). Passing `--font Inter` automatically checks if `"Inter"` is installed in the host OS font catalog and seamlessly falls back to the embedded Inter typeface instead of defaulting to Arial or Segoe UI.
- **Automatic WebM VP9 Alpha Optimization**:
  - Automatically detects `libvpx-vp9` with `yuva420p` and configures row-based multithreading (`-row-mt 1`), speed tuning (`-cpu-used 8`), lookahead (`-lag-in-frames 25`, `-auto-alt-ref 1`), and timeline-friendly keyframe intervals (`-g <fps*8>`) for smooth timeline scrubbing in Premiere Pro, DaVinci Resolve, and Wondershare Filmora.
- **Streamlined `ChatRenderGUI.exe` (Windows WPF Desktop App)**:
  - Launches directly into the **Chat Render** workspace with a real-time SkiaSharp live preview.
  - Removes non-render clutter (VOD/Clip downloaders, chat updaters, queue popups, localization/theme bloat).
  - Adds a **Parallel Segments (`0 = Auto`)** control on the Encoding tab and supports **Direct Multi-File Batch Rendering** when selecting multiple `.json` / `.json.gz` files via **Browse**.

---

## New CLI Flags Summary (In Addition to Standard TwitchDownloader Flags)

| Flag | Default | Description |
| :--- | :--- | :--- |
| `-j`, `--jobs <int>` | `0` (Auto) | Number of parallel FFmpeg encoding segments (`0` = auto-scale by CPU cores, codec, and duration; `1` = single-stream; `2`–`16` = explicit parallel segments stitched losslessly via `ffmpeg -f concat -c copy`). |
| `--giphy <bool>` | `true` | Render bracketed Giphy GIFs (`[Title GIF by Author]`) posted in chat. |
| `--gif-max-height-percent <int>` | `50` | Maximum height of a rendered GIF as a percentage of total chat height (`10`–`100`). |
| `--image-cache-mb <int>` | `256` | Memory budget in MB for off-screen decoded emote/GIF frames before LRU eviction. |

---

## Quickstart

### 1. Build Both Executables (`ChatRenderCLI.exe` & `ChatRenderGUI.exe`)
Run either of the included one-click build scripts on Windows (requires [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)):
- **Command Prompt / Explorer**: Double-click or run `build-win-x64.bat`
- **PowerShell**: `./build-win-x64.ps1`

This produces standalone, self-contained single-file executables in `dist\win-x64\`:
- `dist\win-x64\ChatRenderCLI.exe`
- `dist\win-x64\ChatRenderGUI.exe`

### 2. Ensure FFmpeg is Available
If `ffmpeg.exe` is not already on your system PATH or next to the executable, download it via the CLI:
```cmd
ChatRenderCLI.exe ffmpeg -d
```

### 3. Render via GUI or CLI
- **Desktop GUI**: Launch `ChatRenderGUI.exe`, select one or more `.json` / `.json.gz` chat files, preview your layout in real time, and click **Render**.
- **Command Line**:
  ```cmd
  ChatRenderCLI.exe -i chat.json -o chat.mp4 -w 420 -h 1080 --framerate 60
  ```

---

## Common CLI Workflows & Examples

### 1. High-Speed Transparent WebM Video (Filmora, Premiere Pro, DaVinci Resolve, OBS)
Renders a 60 FPS transparent VP9 alpha overlay using parallel timeline partitioning and direct `yuva420p` planar pipes:
```cmd
ChatRenderCLI.exe -i chat.json -o chat.webm -w 420 -h 1080 --framerate 60 --background-color "#00000000" --output-args "-c:v libvpx-vp9 -crf 18 -b:v 0 -pix_fmt yuva420p \"{save_path}\""
```

### 2. Explicit Parallel Segment Encoding (`-j 4`)
Force 4 concurrent encoding segments on high-core-count CPUs (e.g. Ryzen 9 / Ryzen 7 9800X3D / Core i9):
```cmd
ChatRenderCLI.exe -i chat.json -o chat.webm -w 420 -h 1080 --framerate 60 -j 4 --background-color "#00000000" --output-args "-c:v libvpx-vp9 -crf 18 -b:v 0 -pix_fmt yuva420p \"{save_path}\""
```

### 3. Solid 60 FPS MP4 (H.264) with Outlines
```cmd
ChatRenderCLI.exe -i chat.json -o chat.mp4 -w 420 -h 1080 --framerate 60 --update-rate 0.2 --outline
```

### 4. Dual-Output H.264 + Synchronized Alpha Mask (`--generate-mask`)
For editors that do not support WebM or ProRes alpha channels, generate `chat.mp4` and `chat_mask.mp4` simultaneously:
```cmd
ChatRenderCLI.exe -i chat.json -o chat.mp4 -w 400 -h 900 --generate-mask
```

### 5. Custom Styling & Giphy Memory Tuning
```cmd
ChatRenderCLI.exe -i chat.json -o chat.mp4 -w 450 -h 1080 --background-color "#121214" --alt-background-color "#18181b" --alternate-backgrounds --font "Inter" --font-size 15 --framerate 60 --giphy true --gif-max-height-percent 50 --image-cache-mb 512
```

---

## Documentation Links

- **[Full CLI Reference Manual (`TwitchDownloaderCLI/README.md`)](TwitchDownloaderCLI/README.md)** — Complete documentation for every CLI flag, bitmask, production preset, and invocation mode.
- **[Desktop GUI Guide (`TwitchDownloaderWPF/README.md`)](TwitchDownloaderWPF/README.md)** — Guide to `ChatRenderGUI.exe`, live previewing, parallel segment settings, and multi-file batch rendering.
- **[Build & Compilation Guide (`BUILD_GUIDE.md`)](BUILD_GUIDE.md)** — Instructions for building `ChatRenderCLI.exe` and `ChatRenderGUI.exe` locally or via GitHub Actions.
- **[Architecture & Optimization Plan (`OPTIMIZATION_PLAN.md`)](OPTIMIZATION_PLAN.md)** — Deep-dive technical reference on the multi-phase rendering and encoding optimizations.

---

## Third-Party Credits & Licenses

- **Upstream TwitchDownloader** (© lay295 and contributors) — Original chat rendering architecture and models.
- **SkiaSharp & HarfBuzzSharp** (© Microsoft Corporation, Google LLC) — 2D rasterization and complex text shaping.
- **FFmpeg** (© The FFmpeg developers) — Video muxing, concatenation, and codec encoding.
- **Noto Color Emoji & Twemoji** — Vector and color emoji assets.
- **Inter Font Family** (© Rasmus Andersson) — Modern UI typeface bundled directly into the binaries.
- **HandyControl** — WPF controls for `ChatRenderGUI.exe`.

Full license texts are available in [LICENSE.txt](LICENSE.txt) and `TwitchDownloaderCore/Resources/THIRD-PARTY-LICENSES.txt`.
