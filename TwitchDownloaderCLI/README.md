# ChatRenderCLI Reference Manual

A high-performance command-line tool engineered to render Twitch chat logs (`.json` or `.json.gz`) into video files (`.mp4`, `.webm`, `.mov`, etc.) with full animated emote support, Giphy GIFs, native emoji rendering, and hardware-accelerated 60+ FPS pipelines.

---

## Table of Contents

- [Overview & Invocation Syntax](#overview--invocation-syntax)
- [Global Arguments](#global-arguments)
- [Mode: `chatrender`](#mode-chatrender)
  - [Input & Output](#input--output)
  - [Dimensions & Trimming](#dimensions--trimming)
  - [Framerate & Animation Timing](#framerate--animation-timing)
  - [Colors & Appearance](#colors--appearance)
  - [Typography & Fonts](#typography--fonts)
  - [Emotes, Badges & Giphy GIFs](#emotes-badges--giphy-gifs)
  - [Fine-Grained Layout & Element Scaling](#fine-grained-layout--element-scaling)
  - [Chat Moderation & Filtering](#chat-moderation--filtering)
  - [FFmpeg & Video Encoding Arguments](#ffmpeg--video-encoding-arguments)
  - [System & Temporary Paths](#system--temporary-paths)
- [Mode: `ffmpeg`](#mode-ffmpeg)
- [Mode: `cache`](#mode-cache)
- [Video Production Guides](#video-production-guides)
  - [1. Transparent WebM VP9 Alpha (Filmora, Premiere, DaVinci, OBS)](#1-transparent-webm-vp9-alpha-filmora-premiere-davinci-obs)
  - [2. Apple ProRes 4444 Transparent MOV](#2-apple-prores-4444-transparent-mov)
  - [3. Dual-Stream Alpha Masking (--generate-mask)](#3-dual-stream-alpha-masking---generate-mask)
  - [4. High Frame Rate (60 & 120 FPS) Rendering](#4-high-frame-rate-60--120-fps-rendering)
  - [5. Embedded Giphy GIFs & Memory Management](#5-embedded-giphy-gifs--memory-management)
- [Example Commands](#example-commands)
- [Technical Reference](#technical-reference)
  - [Time Duration Syntax](#time-duration-syntax)
  - [Color Hex Formats](#color-hex-formats)
  - [Badge Filter Bitmask](#badge-filter-bitmask)
  - [Log Level Flags](#log-level-flags)
  - [File Collision Modes](#file-collision-modes)

---

## Overview & Invocation Syntax

ChatRenderCLI supports three execution verbs:
1. **`chatrender`**: Renders chat logs into video.
2. **`ffmpeg`**: Downloads or verifies standalone FFmpeg.
3. **`cache`**: Manages and clears cached emotes, badges, and GIF resolutions.

### Direct Invocation (No Verb Required)
Because rendering is the primary operation, ChatRenderCLI automatically assumes `chatrender` if the first argument is not a known verb. Both of the following commands are completely identical:

```cmd
ChatRenderCLI.exe chatrender -i chat.json -o chat.mp4 -w 420 -h 1080 --framerate 60
ChatRenderCLI.exe -i chat.json -o chat.mp4 -w 420 -h 1080 --framerate 60
```

---

## Global Arguments
Arguments available across all modes.

#### `--banner`
- **Type**: `bool` (Default: `true`)
- Displays application version, copyright, and target runtime info at startup. Disable with `--banner=false`.

#### `--log-level`
- **Type**: `EnumFlags` (Default: `Status,Info,Warning,Error`)
- Controls terminal output verbosity. Applicable values: `None`, `Status`, `Verbose`, `Info`, `Warning`, `Error`, `Ffmpeg`.
- Passing `None` suppresses all logging and implies `--banner=false`. See [Log Level Flags](#log-level-flags) for combining flags.

---

## Mode: `chatrender`

### Input & Output

#### `-i` / `--input` *(REQUIRED)*
- **Type**: `string`
- Absolute or relative path to the source chat log.
- Accepts standard `.json` files or gzip-compressed `.json.gz` archives. Large chat logs (>2 GB) automatically use the streaming deserializer.

#### `-o` / `--output` *(REQUIRED)*
- **Type**: `string`
- Destination path for the rendered video (e.g., `chat.mp4`, `chat.webm`, `chat.mov`).

#### `--collision`
- **Type**: `enum` (Default: `Prompt`)
- Action to take if the output file already exists. Valid values:
  - `Prompt`: Ask user via terminal whether to overwrite.
  - `Overwrite`: Replace the existing file without asking.
  - `Rename`: Append an incremental number (e.g., `chat (1).mp4`).
  - `Exit`: Immediately terminate without overwriting.

---

### Dimensions & Trimming

#### `-w` / `--chat-width`
- **Type**: `int` (Default: `350`)
- Width of the rendered video frame in pixels. Must be an even number when using YUV codecs.

#### `-h` / `--chat-height`
- **Type**: `int` (Default: `600`)
- Height of the rendered video frame in pixels. Must be an even number when using YUV codecs.

#### `-b` / `--beginning`
- **Type**: `TimeDuration` (Default: `-1`, no trim)
- Trims the render to start at a specified offset relative to the beginning of the chat log. See [Time Duration Syntax](#time-duration-syntax).

#### `-e` / `--ending`
- **Type**: `TimeDuration` (Default: `-1`, no trim)
- Trims the render to stop at a specified offset relative to the beginning of the chat log. See [Time Duration Syntax](#time-duration-syntax).

#### `--dispersion`
- **Type**: `bool` (Default: `false`)
- In late 2022, Twitch API timestamps began truncating chat messages to whole seconds. Enabling `--dispersion` uses sub-second comment metadata to disperse message timestamps across the second they were sent. Requires `--update-rate` < 1.0.

---

### Framerate & Animation Timing

#### `--framerate`
- **Type**: `int` (Default: `30`)
- Video output framerate in frames per second (FPS). Common values: `30`, `60`, `120`.
- At high framerates (60+ FPS), the engine's zero-allocation repeat counting, dirty-region emote patching, and O(1) next-transition short-circuiting prevent CPU waste on unchanged ticks.

#### `--update-rate`
- **Type**: `double` (Default: `0.2`)
- Time interval in seconds between chat scroll updates. For example, `0.2` updates the chat scroll position every 200 milliseconds (every 12 ticks at 60 FPS).
- Lower values result in smoother, more continuous chat scrolling; higher values create stepping/chunked scrolling.

#### `--sharpening`
- **Type**: `bool` (Default: `false`)
- Appends FFmpeg `smartblur` sharpening (`smartblur=lr=1:ls=-1.0`). Recommended primarily for high-resolution renders (`font-size` 24+).

#### `--generate-mask`
- **Type**: `bool` (Default: `false`)
- In addition to the primary video output, generates a synchronized black-and-white alpha mask video (`<name>_mask.<ext>`). White pixels represent chat content; black pixels represent transparency. Vectorized via AVX2 / SSSE3 / AdvSimd SIMD intrinsics.

#### `--skip-drive-waiting`
- **Type**: `bool` (Default: `false`)
- Disables output drive synchronization waits between frame dispatches. Can increase render speed by 1–5% on NVMe SSDs and fast local drives.

#### `-j` / `--jobs`
- **Type**: `int` (Default: `0`, auto-scale)
- Number of parallel FFmpeg encoding segments to run concurrently.
- **`0` (Auto)**: Automatically scales based on CPU core count, video duration, and codec (up to `4` parallel segments on 16+ core CPUs for dual-plane `yuva420p` / `--generate-mask` renders, or up to `3` for standard renders). Videos under 60 seconds automatically use a single segment (`1`).
- **`1`**: Forces single-stream sequential encoding (no temporary segment files).
- **`2`–`16`**: Explicitly partitions the video timeline into `N` equal tick segments encoded in parallel and stitched losslessly via `ffmpeg -f concat -c copy` at completion.

---

### Colors & Appearance

#### `--background-color`
- **Type**: `string` (Default: `#111111`)
- Base background color in `#RRGGBB` or `#AARRGGBB` hex format.
- Set to `#00000000` (or `rgba(0,0,0,0)`) for fully transparent video backgrounds.

#### `--alt-background-color`
- **Type**: `string` (Default: `#191919`)
- Alternate message background color when `--alternate-backgrounds` is enabled. Accepts `#RRGGBB` or `#AARRGGBB`.

#### `--highlight-user-color`
- **Type**: `string` (Default: `#C8FF0059`)
- Background tint color applied behind messages sent by users specified in `--highlight-users`. Accepts `#RRGGBB` or `#AARRGGBB`.

#### `--message-color`
- **Type**: `string` (Default: `#ffffff`)
- Default text color for chat message text in `#RRGGBB` or `#AARRGGBB`.

#### `--alternate-backgrounds`
- **Type**: `bool` (Default: `false`)
- Alternates the background color of every other chat comment using `--alt-background-color` to enhance readability.

#### `--readable-colors`
- **Type**: `bool` (Default: `true`)
- Automatically adjusts username contrast against dark/light backgrounds to ensure WCAG readability guidelines are met.

#### `--outline`
- **Type**: `bool` (Default: `false`)
- Draws a distinct solid outline around rendered text and messages, ideal for transparent overlays placed on bright or complex video backgrounds.

#### `--outline-size`
- **Type**: `double` (Default: `4`)
- Stroke thickness in pixels when `--outline` is enabled.

---

### Typography & Fonts

#### `-f` / `--font`
- **Type**: `string` (Default: `Inter Embedded`)
- Font family to use for rendering chat text.
- **Embedded Inter Font Mapping**: ChatRenderCLI bundles the complete **Inter** font family internally. Specifying `--font Inter` or `--font "Inter Embedded"` will automatically use the high-legibility embedded Inter typeface unless a local "Inter" font family is installed on the host operating system.

#### `--font-size`
- **Type**: `double` (Default: `12`)
- Base font size in points for chat messages.

#### `--message-fontstyle`
- **Type**: `string` (Default: `normal`)
- Font weight/style for message text. Options: `normal`, `bold`, `italic`, `bolditalic`.

#### `--username-fontstyle`
- **Type**: `string` (Default: `bold`)
- Font weight/style for chat usernames. Options: `normal`, `bold`, `italic`, `bolditalic`.

#### `--scale-username`
- **Type**: `double` (Default: `1.0`)
- Multiplier to scale username font size relative to the base `--font-size`.

---

### Emotes, Badges & Giphy GIFs

#### `--bttv`
- **Type**: `bool` (Default: `true`)
- Enables BetterTTV (BTTV) static and animated emotes. Disable with `--bttv=false`.

#### `--ffz`
- **Type**: `bool` (Default: `true`)
- Enables FrankerFaceZ (FFZ) static and animated emotes. Disable with `--ffz=false`.

#### `--stv`
- **Type**: `bool` (Default: `true`)
- Enables 7TV static and animated emotes. Disable with `--stv=false`.

#### `--allow-unlisted-emotes`
- **Type**: `bool` (Default: `true`)
- Allows unlisted 7TV emotes to be fetched and rendered.

#### `--giphy`
- **Type**: `bool` (Default: `true`)
- Enables rendering of embedded or linked Giphy GIFs posted in Twitch chat (e.g., `[Horse Stare GIF by Jan Metternich]`). GIFs are displayed full-width below the username.

#### `--gif-max-height-percent`
- **Type**: `int` (Default: `50`)
- Caps the rendered height of a GIF as a percentage of the total `--chat-height`. A value of `50` ensures a GIF never occupies more than half of the visible chat window; `100` allows full-height GIFs.

#### `--image-cache-mb`
- **Type**: `int` (Default: `256`)
- Memory budget in megabytes for caching decoded off-screen emote and GIF frames. Active on-screen frames are always retained. Off-screen frames are evicted via an LRU policy when the budget is reached.

#### `--sub-messages`
- **Type**: `bool` (Default: `true`)
- Enables rendering of subscription and re-subscription announcement notices and highlight bars.

#### `--badges`
- **Type**: `bool` (Default: `true`)
- Enables rendering of user badges (Subscriber, Moderator, VIP, Turbo, Bits, etc.).

#### `--badge-filter`
- **Type**: `int` (Default: `0`)
- Bitmask integer to filter out specific badge types. See [Badge Filter Bitmask](#badge-filter-bitmask) for calculation details.

#### `--avatars`
- **Type**: `bool` (Default: `false`)
- Renders user profile avatars to the left of badges and usernames.

#### `--emoji-vendor`
- **Type**: `string` (Default: `notocolor`)
- Color emoji vendor font. Options:
  - `notocolor` / `google`: Bundled Google Noto Color Emoji.
  - `twemoji` / `twitter`: Bundled Twitter Twemoji.
  - `system` / `none`: Fall back to host operating system emoji font.

#### `--offline`
- **Type**: `bool` (Default: `false`)
- Disables all external HTTP network requests. Renders strictly from assets pre-embedded inside the chat JSON archive.

---

### Fine-Grained Layout & Element Scaling

Customize visual spacing, padding, and proportions with multiplier floats (Default `1.0`):

| Option | Default | Description |
| :--- | :--- | :--- |
| `--scale-emote` | `1.0` | Multiplier for emote image size. |
| `--scale-badge` | `1.0` | Multiplier for badge icon size. |
| `--scale-emoji` | `1.0` | Multiplier for emoji icon size. |
| `--scale-avatar` | `1.0` | Multiplier for user avatar icon size. |
| `--scale-vertical` | `1.0` | Multiplier for vertical line spacing between messages. |
| `--scale-side-padding` | `1.0` | Multiplier for left/right margin padding inside the canvas. |
| `--scale-section-height` | `1.0` | Multiplier for overall comment section vertical bounding box. |
| `--scale-word-space` | `1.0` | Spacing between individual words. |
| `--scale-emote-space` | `1.0` | Spacing between consecutive emotes. |
| `--scale-highlight-stroke` | `1.0` | Thickness of the subscription accent highlight line. |
| `--scale-highlight-indent` | `1.0` | Indentation margin for subscription accent highlight blocks. |

---

### Chat Moderation & Filtering

#### `--ignore-users`
- **Type**: `string` (Default: `""`)
- Comma-separated list of usernames to omit completely from the render (case-insensitive).
- Example: `--ignore-users "Nightbot,StreamElements,Moobot"`

#### `--ban-words`
- **Type**: `string` (Default: `""`)
- Comma-separated list of words or phrases to filter. Any message containing a banned word will be omitted (case-insensitive).
- Example: `--ban-words "free followers,buy viewers"`

#### `--highlight-users`
- **Type**: `string` (Default: `""`)
- Comma-separated list of usernames whose comments will be rendered with the `--highlight-user-color` background.
- Example: `--highlight-users "Broadcaster,VIPUser"`

---

### FFmpeg & Video Encoding Arguments

#### `--input-args`
- **Type**: `string`
- Arguments passed to FFmpeg before `-i -`.
- Default: `-framerate {fps} -f rawvideo -analyzeduration {max_int} -probesize {max_int} -pix_fmt {pix_fmt} -video_size {width}x{height} -i -`
- Supported template tokens: `{fps}`, `{max_int}`, `{pix_fmt}`, `{width}`, `{height}`.
- ChatRenderCLI automatically manages `{pix_fmt}`: when writing planar YUV, it automatically selects `yuva420p` or `yuv420p` to eliminate FFmpeg color conversion overhead.

#### `--output-args`
- **Type**: `string`
- Arguments passed to FFmpeg after the input stream to configure video encoding.
- Default: `-c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p "{save_path}"`
- Supported template tokens: `{save_path}`.
- **VP9 Tuning**: When `libvpx-vp9` is used, ChatRenderCLI automatically detects it and injects hardware-optimized flags (`-row-mt 1`, `-cpu-used 4`, `-deadline good`, `-lag-in-frames 25`, `-auto-alt-ref 1`, and `-g <fps*8>`).

---

### System & Temporary Paths

#### `--ffmpeg-path`
- **Type**: `string` (Default: auto-detected from PATH or working directory)
- Explicit file path to a standalone `ffmpeg` executable.

#### `--temp-path`
- **Type**: `string` (Default: system temporary directory)
- Path to temporary folder used for caching downloaded emotes, badges, and GIF files.

---

## Mode: `ffmpeg`

Manage standalone FFmpeg executables without needing manual installations.

#### `-d` / `--download`
- **Type**: `bool` (Default: `false`)
- Downloads an official, portable FFmpeg binary matching your operating system directly into the working directory.

```cmd
ChatRenderCLI.exe ffmpeg -d
```

---

## Mode: `cache`

Manage the local asset cache containing downloaded emotes, badges, avatars, and resolved Giphy titles.

#### `-c` / `--clear`
- Prompts for confirmation and deletes all cached assets in the temporary directory.

#### `--force-clear`
- Deletes all cached assets immediately without prompting for confirmation.

```cmd
ChatRenderCLI.exe cache -c
ChatRenderCLI.exe cache --force-clear
```

---

## Video Production Guides

### 1. Transparent WebM VP9 Alpha (Filmora, Premiere, DaVinci, OBS)
To render a chat overlay with true alpha transparency for seamless compositing onto your video track:

```cmd
ChatRenderCLI.exe -i chat.json -o chat.webm -w 420 -h 1080 --framerate 60 --background-color "#00000000" --output-args "-c:v libvpx-vp9 -crf 18 -b:v 0 -pix_fmt yuva420p \"{save_path}\""
```

#### Why This Is Fast:
- **Planar `yuva420p` Pipe**: ChatRenderCLI automatically detects `-pix_fmt yuva420p` and converts frames directly in C#, cutting pipe bandwidth from 4 bytes/pixel down to 2.5 bytes/pixel.
- **Zero Redundant Swscale**: FFmpeg receives native `yuva420p` frames without executing CPU-heavy `swscale` filters on every tick.
- **Dynamic GOP Scaling**: ChatRenderCLI sets `-g` to `framerate * 8` (e.g. `-g 480` at 60 FPS), maintaining fast timeline scrubbing in Wondershare Filmora 15 and DaVinci Resolve without bloating file sizes.
- **Row-Based Multithreading & Parallel Segments**: Automatically adds `-row-mt 1` and `-cpu-used 8`, and partitions the timeline across parallel FFmpeg workers (`-j` / `--jobs`) to saturate all CPU cores even when `libvpx-vp9` hits its per-process tile/alpha threading ceiling.

---

### 2. Apple ProRes 4444 Transparent MOV
For editing on macOS or workflows requiring lossless intra-frame compression with alpha:

```bash
ChatRenderCLI -i chat.json -o chat.mov -w 420 -h 1080 --framerate 60 --background-color "#00000000" --output-args "-c:v prores_ks -profile:v 4 -pix_fmt yuva444p10le \"{save_path}\""
```

---

### 3. Dual-Stream Alpha Masking (`--generate-mask`)
If your video editing suite does not support WebM VP9 or ProRes alpha channels, use standard H.264 with `--generate-mask`:

```cmd
ChatRenderCLI.exe -i chat.json -o chat.mp4 -w 400 -h 1000 --generate-mask
```

This outputs two synchronized files:
1. `chat.mp4`: The chat video with solid background.
2. `chat_mask.mp4`: High-contrast grayscale mask video (white = text/emotes, black = background).

In your video editor, place `chat.mp4` on track 2, `chat_mask.mp4` on track 3, and set the composite track matte mode to **Luma Matte**.

---

### 4. High Frame Rate (60 & 120 FPS) Rendering
Doubling framerate from 30 FPS to 60 FPS doubles tick counts, but **does not** double chat updates. ChatRenderCLI uses several specialized optimizations:

1. **O(1) Next-Transition Short-Circuiting**: ChatRenderCLI inspects active animated emotes and calculates `_animNextChangeTickMs`. If no emote changes frame on the current tick, the entire emote scan and composition is bypassed in 0.001 ms.
2. **Incremental Row Patching**: Only the specific rows spanning advancing animated emotes are converted to YUV; solid or unchanged rows are preserved.
3. **Hardware Queue Sizing**: Rawvideo input buffers automatically scale to `-thread_queue_size 1024` to prevent pipe stalls.

---

### 5. Embedded Giphy GIFs & Memory Management
When chat contains Giphy GIFs, ChatRenderCLI handles them with zero extra user intervention:
- Giphy titles are resolved concurrently (8 at a time) and cached in `giphy/resolved.json.gz`.
- When rendering huge 8-hour archives with hundreds of unique GIFs, `--image-cache-mb 256` prevents out-of-memory errors by retaining active visible GIFs and automatically evicting old off-screen frames from RAM.

---

## Example Commands

#### Standard High-Quality MP4
```cmd
ChatRenderCLI.exe -i chat.json -o chat.mp4 -w 420 -h 1080 --framerate 60 --update-rate 0.2
```

#### Transparent WebM with Outlines (Ideal for Gameplay Overlay)
```cmd
ChatRenderCLI.exe -i chat.json -o chat.webm -w 400 -h 1080 --framerate 60 --background-color "#00000000" --outline --outline-size 3 --output-args "-c:v libvpx-vp9 -crf 18 -b:v 0 -pix_fmt yuva420p \"{save_path}\""
```

#### Clean Dark Mode with Alternating Rows
```cmd
ChatRenderCLI.exe -i chat.json -o chat.mp4 -w 450 -h 900 --background-color "#0E0E10" --alt-background-color "#18181B" --alternate-backgrounds --font "Inter" --font-size 14
```

#### Moderated Chat (Hiding Bots, Stream Elements, and Moderator Badges)
```cmd
ChatRenderCLI.exe -i chat.json -o chat.mp4 --ignore-users "StreamElements,Nightbot,Moobot" --badge-filter 4
```

#### Segment Render (Render 15 Minutes Only)
```cmd
ChatRenderCLI.exe -i chat.json -o highlight_chat.mp4 -b 00:15:00 -e 00:30:00
```

#### Fully Offline Render from Archived JSON
```cmd
ChatRenderCLI.exe -i archived_chat.json.gz -o chat.mp4 --offline
```

---

## Technical Reference

### Time Duration Syntax
Trimming arguments (`-b` / `--beginning` and `-e` / `--ending`) accept:
- **Seconds**: `45` or `45s`
- **Milliseconds**: `500ms`
- **Minutes**: `10m`
- **Hours**: `2h`
- **Timestamp Strings**: `mm:ss` (e.g., `04:30`) or `hh:mm:ss` (e.g., `01:15:30.500`)

### Color Hex Formats
Color arguments (`--background-color`, `--alt-background-color`, `--highlight-user-color`, `--message-color`) accept:
- 6-digit RGB: `#RRGGBB` (e.g., `#111111`, `#FFFFFF`)
- 8-digit ARGB: `#AARRGGBB` (e.g., `#00000000` for 100% transparent, `#80000000` for 50% semi-transparent black)

### Badge Filter Bitmask
To hide multiple badge categories, sum their corresponding integer values and pass the result to `--badge-filter`:

| Category | Value |
| :--- | :--- |
| Other | `1` |
| Broadcaster | `2` |
| Moderator | `4` |
| VIP | `8` |
| Subscriber | `16` |
| Predictions | `32` |
| NoAudio / NoVideo | `64` |
| Prime Gaming | `128` |

*Example*: To filter Broadcaster (`2`) and Moderator (`4`), pass `--badge-filter 6`.

### Log Level Flags
Combine flags with commas:
`--log-level Status,Info,Warning,Error`
Valid flags: `None`, `Status`, `Verbose`, `Info`, `Warning`, `Error`, `Ffmpeg`.

### File Collision Modes
`--collision Overwrite` | `--collision Exit` | `--collision Rename` | `--collision Prompt`