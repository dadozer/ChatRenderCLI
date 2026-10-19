# ChatRenderCLI — Hardware-Adaptive Performance Optimization Plan

This document is the permanent architectural reference for the four-phase optimization of `ChatRenderCLI` and `TwitchDownloaderCore`. Every phase is engineered to preserve **100% visual parity**, **100% CLI drop-in compatibility**, and **full Giphy + 7TV/BTTV/FFZ/Twitch emote support**, while scaling dynamically from laptop CPUs up to high-core-count desktop workstations (**AMD Ryzen 7 9800X3D with 96MB 3D V-Cache & AVX-512 + NVIDIA RTX 3080 Ti**).

---

## Confirmed Architectural Decisions

1. **WebM VP9 Alpha Pipeline Compatibility**:
   - Retain `.webm` (`libvpx-vp9` with `yuva420p` alpha channel) compatibility for downstream VOD compositing while decoupling C# frame generation from FFmpeg `stdin` using an asynchronous ring-buffered pipe writer and optimizing rawvideo input/VP9 threading parameters.
2. **Hardware-Adaptive Multithreading**:
   - Scale concurrency dynamically via `Environment.ProcessorCount` and runtime SIMD intrinsics (`Avx512BW`, `Avx2`, `Ssse3`, `AdvSimd`) so the engine saturates an 8C/16T desktop processor without overloading a lower-power laptop.
3. **Smart `"Inter"` Font Resolution**:
   - When `--font Inter` is passed on the command line, inspect system fonts via `SKFontManager` to check if the `"Inter"` family is actually installed; if not installed on the machine, automatically use the built-in embedded Inter typeface (`Inter Embedded`) instead of silently falling back to a default OS font.

---

## System Architecture Diagram

```
┌──────────────────────────────────────────────────────────────────────────────┐
│                     PHASE 1: STARTUP & ASSET PIPELINE                        │
│  ┌─────────────────────────┐   ┌─────────────────────────┐   ┌────────────┐  │
│  │ Single-Pass Comment     │   │ Parallel Disk/HTTP      │   │ On-Demand  │  │
│  │ Token & Emoji Indexer   │──▶│ Emote/Badge/GIF Loader  │──▶│ Emoji      │  │
│  │ (O(N) Span Tokenization)│   │ (Scaled by Core Count)  │   │ Decoder    │  │
│  └─────────────────────────┘   └─────────────────────────┘   └────────────┘  │
└───────────────────────────────────────┬──────────────────────────────────────┘
                                        │
                                        ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                PHASE 3: LOOKAHEAD PRE-RENDER & DECODE WORKERS                │
│  ┌───────────────────────────────────┐  ┌─────────────────────────────────┐  │
│  │ Background CommentSection Workers │  │ Background Emote/GIF Frame      │  │
│  │ • Parallel Text Layout & Outlines │  │ Extraction & High-Quality Scale │  │
│  │ • Direct Single-Line Rasterization│  │ • Sliding Lookahead Window      │  │
│  └─────────────────┬─────────────────┘  └────────────────┬────────────────┘  │
└────────────────────┼─────────────────────────────────────┼───────────────────┘
                     └──────────────────┬──────────────────┘
                                        ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                 PHASE 2: INCREMENTAL TICK COMPOSITOR (HOT LOOP)              │
│  ┌───────────────────────────────────┐  ┌─────────────────────────────────┐  │
│  │ Incremental UpdateFrame Scroller  │  │ Dirty-Rect Animation Compositor │  │
│  │ • Blits existing chat upward      │─▶│ • Restores only emote/GIF rects │  │
│  │ • Draws only new bottom comments  │  │ • Skips unchanged anim ticks    │  │
│  └───────────────────────────────────┘  └────────────────┬────────────────┘  │
└──────────────────────────────────────────────────────────┼───────────────────┘
                                                           │
                                                           ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                   PHASE 4: ASYNC FFMPEG PIPE & ENCODER I/O                   │
│  ┌───────────────────────────────────┐  ┌─────────────────────────────────┐  │
│  │ Pooled Frame Ring Buffer          │  │ Dedicated Pipe Writer Thread    │  │
│  │ • Lock-free Producer/Consumer     │─▶│ • Async large-buffer stdin write│  │
│  │ • AVX-512 / AVX2 Alpha Masking    │  │ • Feeds FFmpeg (VP9 / NVENC)    │  │
│  └───────────────────────────────────┘  └─────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────────────────┘
```

---

## Phased Implementation Roadmap

### Phase 1 — Startup, Font Resolution & Asset Loading Optimization *(Current Phase)*
- **Smart `"Inter"` Font Resolution**:
  - Detect whether `"Inter"` is installed in the OS font catalog when `--font Inter` is requested; if missing, automatically bind to the embedded `Inter` typeface resources (`Inter`, `InterBold`, `InterItalic`, `InterBoldItalic`).
- **Single-Pass $\mathcal{O}(\text{Comments})$ Word & Cheer Token Indexing**:
  - Replace the $\mathcal{O}(\text{Emotes} \times \text{Comments})$ per-emote regex scans in `TwitchHelper.GetThirdPartyEmotes` and `TwitchHelper.GetBits` with a single zero-allocation `ReadOnlySpan<char>` tokenization pass over comment bodies into a `HashSet<string>` (using `AlternateLookup<ReadOnlySpan<char>>`).
- **On-Demand Emoji Indexing & Lazy Decoding/Scaling**:
  - Index all ~3,689 Twemoji/NotoColor Unicode sequences from filenames in under 1 ms without decoding all 3,689 PNGs up front. Decode and cubic-scale (`SKFilterQuality.High`) only the emojis that actually appear in the chat log on demand.
- **Hardware-Adaptive Concurrent Asset & Metadata Fetching**:
  - Fetch BTTV, FFZ, and 7TV metadata concurrently (`Task.WhenAll`).
  - Fetch global and channel badge metadata concurrently (`Task.WhenAll`) and filter badge versions to those actually present in the chat comments.
  - Parallelize disk cache reads, `SKCodec` header parsing, and HTTP downloads across `GetEmotes`, `GetThirdPartyEmotes`, `GetChatBadges`, `GetGiphyGifs`, `GetBits`, and `GetAvatars` scaled by `Environment.ProcessorCount`.
  - Eliminate redundant per-file `DirectoryInfo.Refresh()` and duplicate `File.Exists()` syscalls in `TwitchHelper.GetImage`.
  - Remove unconditional double `GC.Collect()` + `GC.WaitForPendingFinalizers()` stalls when `embeddedData` is not present.

### Phase 2 — Memory Bandwidth, Incremental Scrolling & Dirty-Rect Compositing
- **Single-Line `SectionImage` Fast-Path**:
  - Avoid redundant intermediate bitmap blits in `CombineImages` for single-line comments and reduce pixel copying when converting pooled `SectionImage` bitmaps into `SKImage`.
- **Incremental `UpdateFrame` Canvas Scrolling**:
  - Shift still-visible upper chat regions upward in-place when new comments arrive and draw only newly revealed comments at the bottom.
- **Dirty-Rectangle Animated Emote & Giphy Compositing**:
  - In `ComposeAnimatedFrame`, restore only the bounding rectangles of animated emotes/GIFs from `updateFrame` onto `_animComposedFrame` rather than copying the full frame buffer on every animated tick.
- **Hot-Path Allocation Elimination**:
  - Eliminate per-comment string concatenations in `DrawUsername`, per-comment list allocations in `ParseCommentBadges`, redundant `RtlRegex.IsMatch` checks in `DrawText`, and full-dictionary `DecodedImages()` enumeration in `ImageMemoryBudget`.

### Phase 3 — Hardware-Adaptive Multi-Threaded Lookahead Pipeline
- **Background `CommentSection` Pre-Renderer**:
  - Pre-render upcoming `CommentSection` layouts, text outlines (`GetTextPath` + `DrawPath`), and static elements across a bounded sliding window of worker threads scaled to `Environment.ProcessorCount`.
- **Background Emote & Giphy Frame Pre-Decoder**:
  - Decode and scale multi-frame animated emotes and Giphy GIFs asynchronously ahead of the render tick cursor so `ExtractFrames()` never stalls the main tick loop.

### Phase 4 — Async FFmpeg Pipe Writer, AVX-512 Masking & VP9 Alpha Tuning
- **Asynchronous Ring-Buffered FFmpeg Pipe Writer**:
  - Decouple `ffmpegStream.Write` from the main render thread using a bounded pool of frame buffers drained by a dedicated pipe-writer thread.
- **Native AVX-512 Alpha Extraction (`SetFrameMask`)**:
  - Add an `Avx512BW` / `Avx512Vbmi` fast-path processing 16 pixels (64 bytes) per iteration on AVX-512 CPUs while preserving `Avx2`, `Ssse3`, `AdvSimd`, and scalar fallbacks.
- **Throttled Drive Readiness & Rawvideo Pipe Startup**:
  - Throttle `DriveHelper.WaitForDrive` checks to time-based intervals and optimize FFmpeg rawvideo pipe buffering and `libvpx-vp9` alpha encoding parameters.
