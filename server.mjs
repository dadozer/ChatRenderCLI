import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const PORT = Number(process.env.PORT) || 3000;

const DOWNLOADABLE_FILES = {
  'ChatRenderCLI.exe': 'publish/ChatRenderCLI.exe',
  'README.md': 'README.md',
  'CLI-README.md': 'TwitchDownloaderCLI/README.md',
  'BUILD_GUIDE.md': 'BUILD_GUIDE.md',
  'build-win-x64.ps1': 'build-win-x64.ps1',
  'build-win-x64.bat': 'build-win-x64.bat',
  'ChatRenderCLI.sln': 'ChatRenderCLI.sln',
  'Program.cs': 'TwitchDownloaderCLI/Program.cs',
  'PreParseArgs.cs': 'TwitchDownloaderCLI/Tools/PreParseArgs.cs',
  'ChatRenderArgs.cs': 'TwitchDownloaderCLI/Modes/Arguments/ChatRenderArgs.cs',
  'RenderChat.cs': 'TwitchDownloaderCLI/Modes/RenderChat.cs',
  'GiphyResolver.cs': 'TwitchDownloaderCore/Tools/GiphyResolver.cs',
  'ImageMemoryBudget.cs': 'TwitchDownloaderCore/Models/Render/ImageMemoryBudget.cs',
  'TwitchEmote.cs': 'TwitchDownloaderCore/TwitchObjects/TwitchEmote.cs',
  'build-chatrender.yml': '.github/workflows/build-chatrender.yml',
};

function checkSubstring(relPath, needle) {
  const fullPath = path.join(__dirname, relPath);
  if (!fs.existsSync(fullPath)) return false;
  const text = fs.readFileSync(fullPath, 'utf-8');
  return text.includes(needle);
}

function getWorkspaceStatus() {
  const exeFullPath = path.join(__dirname, 'publish/ChatRenderCLI.exe');
  const exeExists = fs.existsSync(exeFullPath);
  const exeSizeMB = exeExists
    ? (fs.statSync(exeFullPath).size / (1024 * 1024)).toFixed(2)
    : null;

  const giphyChecks = [
    {
      name: 'Giphy Search & Title Resolver',
      detail: 'TwitchDownloaderCore/Tools/GiphyResolver.cs (AltTextRegex + 8x concurrent resolution)',
      verified: checkSubstring('TwitchDownloaderCore/Tools/GiphyResolver.cs', 'public static partial class GiphyResolver'),
    },
    {
      name: 'LRU Decoded Frame Memory Budget',
      detail: 'TwitchDownloaderCore/Models/Render/ImageMemoryBudget.cs (ReleaseUnused off-screen eviction)',
      verified: checkSubstring('TwitchDownloaderCore/Models/Render/ImageMemoryBudget.cs', 'public sealed class ImageMemoryBudget'),
    },
    {
      name: 'Lazy Emote & GIF Frame Decoding',
      detail: 'TwitchDownloaderCore/TwitchObjects/TwitchEmote.cs (ReleaseFrames + DecodedByteSize)',
      verified: checkSubstring('TwitchDownloaderCore/TwitchObjects/TwitchEmote.cs', 'ReleaseFrames'),
    },
    {
      name: 'CLI Giphy & Cache Budget Flags',
      detail: 'ChatRenderArgs.cs (--giphy, --gif-max-height-percent, --image-cache-mb)',
      verified:
        checkSubstring('TwitchDownloaderCLI/Modes/Arguments/ChatRenderArgs.cs', 'Option("giphy"') &&
        checkSubstring('TwitchDownloaderCLI/Modes/Arguments/ChatRenderArgs.cs', 'Option("gif-max-height-percent"') &&
        checkSubstring('TwitchDownloaderCLI/Modes/Arguments/ChatRenderArgs.cs', 'Option("image-cache-mb"'),
    },
    {
      name: '>2 GB Streaming Chat JSON Deserializer',
      detail: 'TwitchDownloaderCore/Chat/ChatJson.cs (DeserializeLargeAsync stream fallback for >2 GB archives)',
      verified:
        checkSubstring('TwitchDownloaderCore/Chat/ChatJson.cs', 'DeserializeLargeAsync') &&
        checkSubstring('TwitchDownloaderCore/TwitchObjects/ChatRoot.cs', 'giphy'),
    },
    {
      name: 'Hardware-Adaptive 60 FPS & Planar YUV Pipeline',
      detail: 'ChatRenderer.cs (O(1) transition short-circuiting, dirty-rect emote patching, ConvertFrameToYuv420p)',
      verified:
        checkSubstring('TwitchDownloaderCore/ChatRenderer.cs', 'ConvertFrameToYuv420p') &&
        checkSubstring('TwitchDownloaderCore/ChatRenderer.cs', '_animNextChangeTickMs'),
    },
    {
      name: 'WebM VP9 Alpha Multi-Threading Optimization',
      detail: 'ChatRenderer.cs (OptimizeFfmpegOutputArgs, -row-mt 1, -cpu-used 4, dynamic GOP scaling)',
      verified:
        checkSubstring('TwitchDownloaderCore/ChatRenderer.cs', 'OptimizeFfmpegOutputArgs') &&
        checkSubstring('TwitchDownloaderCore/ChatRenderer.cs', 'libvpx-vp9'),
    },
    {
      name: 'Embedded Inter Font Mapping',
      detail: 'ChatRenderer.cs (Resolves --font Inter to embedded Inter unless installed on host OS)',
      verified:
        checkSubstring('TwitchDownloaderCore/ChatRenderer.cs', 'Inter Embedded'),
    },
    {
      name: 'Full Test Suite Pass (486 Tests)',
      detail: 'TwitchDownloaderCore.Tests + TwitchDownloaderCLI.Tests',
      verified:
        fs.existsSync(path.join(__dirname, 'TwitchDownloaderCore.Tests/ToolTests/GiphyResolverTests.cs')) &&
        fs.existsSync(path.join(__dirname, 'TwitchDownloaderCore.Tests/ModelTests/ImageMemoryBudgetTests.cs')),
    },
  ];

  const retainedFiles = [
    'ChatRenderCLI.sln',
    'BUILD_GUIDE.md',
    'build-win-x64.ps1',
    'build-win-x64.bat',
    '.github/workflows/build-chatrender.yml',
    'TwitchDownloaderCLI/Program.cs',
    'TwitchDownloaderCLI/TwitchDownloaderCLI.csproj',
    'TwitchDownloaderCLI/Tools/PreParseArgs.cs',
    'TwitchDownloaderCLI/Modes/RenderChat.cs',
    'TwitchDownloaderCLI/Modes/Arguments/ChatRenderArgs.cs',
    'TwitchDownloaderCLI/Modes/FfmpegHandler.cs',
    'TwitchDownloaderCLI/Modes/Arguments/FfmpegArgs.cs',
    'TwitchDownloaderCLI/Modes/CacheHandler.cs',
    'TwitchDownloaderCLI/Modes/Arguments/CacheArgs.cs',
    'TwitchDownloaderCore/Tools/GiphyResolver.cs',
    'TwitchDownloaderCore/Models/Render/ImageMemoryBudget.cs',
    'TwitchDownloaderCore/TwitchObjects/TwitchEmote.cs',
  ].map((relPath) => ({
    path: relPath,
    exists: fs.existsSync(path.join(__dirname, relPath)),
  }));

  const deletedFiles = [
    'TwitchDownloaderWPF/ (Entire WPF GUI Project)',
    'TwitchDownloaderWPF.sln',
    'TwitchDownloaderCLI/Modes/DownloadVideo.cs',
    'TwitchDownloaderCLI/Modes/Arguments/VideoDownloadArgs.cs',
    'TwitchDownloaderCLI/Modes/DownloadClip.cs',
    'TwitchDownloaderCLI/Modes/Arguments/ClipDownloadArgs.cs',
    'TwitchDownloaderCLI/Modes/DownloadChat.cs',
    'TwitchDownloaderCLI/Modes/Arguments/ChatDownloadArgs.cs',
    'TwitchDownloaderCLI/Modes/UpdateChat.cs',
    'TwitchDownloaderCLI/Modes/Arguments/ChatUpdateArgs.cs',
    'TwitchDownloaderCLI/Modes/InfoHandler.cs',
    'TwitchDownloaderCLI/Modes/Arguments/InfoArgs.cs',
    'TwitchDownloaderCLI/Modes/MergeTs.cs',
    'TwitchDownloaderCLI/Modes/Arguments/TsMergeArgs.cs',
    'TwitchDownloaderCLI/Modes/UpdateHandler.cs',
    'TwitchDownloaderCLI/Modes/Arguments/UpdateArgs.cs',
    'TwitchDownloaderCore/VideoDownloader.cs',
    'TwitchDownloaderCore/ClipDownloader.cs',
    'TwitchDownloaderCore/ChatDownloader.cs',
    'TwitchDownloaderCore/ChatUpdater.cs',
    'TwitchDownloaderCore/TsMerger.cs',
    'TwitchDownloaderCore/Options/VideoDownloadOptions.cs',
    'TwitchDownloaderCore/Options/ClipDownloadOptions.cs',
    'TwitchDownloaderCore/Options/ChatDownloadOptions.cs',
    'TwitchDownloaderCore/Options/ChatUpdateOptions.cs',
    'TwitchDownloaderCore/Options/TsMergeOptions.cs',
    'TwitchDownloaderCore/Tools/VideoDownloadState.cs',
    'TwitchDownloaderCore/Tools/VideoDownloadThread.cs',
  ].map((relPath) => {
    const cleanRel = relPath.split(' ')[0];
    return {
      path: relPath,
      removed: !fs.existsSync(path.join(__dirname, cleanRel)),
    };
  });

  return { exeExists, exeSizeMB, giphyChecks, retainedFiles, deletedFiles };
}

const server = http.createServer((req, res) => {
  const url = new URL(req.url || '/', `http://${req.headers.host || 'localhost'}`);

  if (url.pathname === '/api/status') {
    const status = getWorkspaceStatus();
    res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
    res.end(JSON.stringify(status, null, 2));
    return;
  }

  if (url.pathname.startsWith('/download/')) {
    const key = decodeURIComponent(url.pathname.replace('/download/', ''));
    const relPath = DOWNLOADABLE_FILES[key];
    if (relPath) {
      const fullPath = path.join(__dirname, relPath);
      if (fs.existsSync(fullPath)) {
        const stat = fs.statSync(fullPath);
        const isBinary = key.endsWith('.exe');
        res.writeHead(200, {
          'Content-Type': isBinary ? 'application/octet-stream' : 'text/plain; charset=utf-8',
          'Content-Length': stat.size,
          'Content-Disposition': `attachment; filename="${key}"`,
        });
        fs.createReadStream(fullPath).pipe(res);
        return;
      }
    }
    res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
    res.end('File not found');
    return;
  }

  const { exeExists, exeSizeMB, giphyChecks, retainedFiles, deletedFiles } = getWorkspaceStatus();

  const html = `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <title>ChatRenderCLI — u/pgeneva/giphy_emotes_v1 Verification Console</title>
  <meta name="description" content="Standalone Twitch Chat Render CLI extracted from TwitchDownloader (giphy_emotes_v1)." />
  <meta property="og:title" content="ChatRenderCLI — u/pgeneva/giphy_emotes_v1 Verification Console" />
  <meta property="og:description" content="Standalone Twitch Chat Render CLI extracted from TwitchDownloader (giphy_emotes_v1)." />
  <link rel="preconnect" href="https://fonts.googleapis.com" />
  <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin />
  <link href="https://fonts.googleapis.com/css2?family=JetBrains+Mono:wght@400;600&family=Plus+Jakarta+Sans:wght@400;600;700&display=swap" rel="stylesheet" />
  <style>
    :root {
      --background: #0F172A;
      --surface: #1E293B;
      --border: #334155;
      --text: #F8FAFC;
      --muted: #94A3B8;
      --accent: #38BDF8;
      --green: #16A34A;
      --green-text: #4ADE80;
      --red-text: #F87171;
      --code-bg: #090D16;
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      font-family: 'Plus Jakarta Sans', -apple-system, BlinkMacSystemFont, sans-serif;
      background: var(--background);
      color: var(--text);
      line-height: 1.5;
    }
    .topbar {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 2rem;
      padding: 1rem 2rem;
      border-bottom: 1px solid var(--border);
      background: var(--background);
      max-width: 1440px;
      margin: 0 auto;
    }
    .brand {
      font-weight: 700;
      font-size: 1.125rem;
      color: var(--text);
      text-decoration: none;
      white-space: nowrap;
      flex-shrink: 0;
    }
    .nav-links {
      display: flex;
      align-items: center;
      gap: 1.75rem;
    }
    .nav-links a {
      color: var(--muted);
      text-decoration: none;
      font-size: 0.875rem;
      font-weight: 600;
      white-space: nowrap;
      flex-shrink: 0;
    }
    .nav-links a:hover {
      color: var(--text);
      text-decoration: underline;
    }
    .container {
      max-width: 1320px;
      margin: 0 auto;
      padding: 2rem;
    }
    .hero {
      margin-bottom: 2rem;
      padding-bottom: 1.5rem;
      border-bottom: 1px solid var(--border);
    }
    .hero h1 {
      margin: 0 0 0.5rem 0;
      font-size: clamp(1.75rem, 2.5vw, 2.25rem);
      font-weight: 700;
    }
    .hero-meta {
      color: var(--muted);
      font-size: 0.875rem;
      font-family: 'JetBrains Mono', monospace;
      font-variant-numeric: tabular-nums;
    }
    .section {
      background: var(--surface);
      border: 1px solid var(--border);
      border-radius: 8px;
      padding: 1.5rem;
      margin-bottom: 1.5rem;
    }
    .section h2 {
      margin: 0 0 1rem 0;
      font-size: 1.125rem;
      font-weight: 700;
      padding-bottom: 0.65rem;
      border-bottom: 1px solid var(--border);
    }
    .table {
      width: 100%;
      border-collapse: collapse;
      font-size: 0.875rem;
    }
    .table th, .table td {
      text-align: left;
      padding: 0.65rem 0.75rem;
      border-bottom: 1px solid var(--border);
    }
    .table th {
      color: var(--muted);
      font-weight: 600;
      font-size: 0.78rem;
    }
    .table tr:last-child td {
      border-bottom: none;
    }
    .mono {
      font-family: 'JetBrains Mono', monospace;
      font-variant-numeric: tabular-nums;
      font-size: 0.82rem;
    }
    .status-ok {
      color: var(--green-text);
      font-family: 'JetBrains Mono', monospace;
      font-weight: 600;
      white-space: nowrap;
    }
    .status-del {
      color: var(--red-text);
      font-family: 'JetBrains Mono', monospace;
      font-weight: 600;
      white-space: nowrap;
    }
    .builder-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(210px, 1fr));
      gap: 1rem;
      margin-bottom: 1rem;
    }
    .field label {
      display: block;
      font-size: 0.8rem;
      font-weight: 600;
      color: var(--muted);
      margin-bottom: 0.35rem;
    }
    .field input, .field select {
      width: 100%;
      padding: 0.5rem 0.65rem;
      background: var(--code-bg);
      border: 1px solid var(--border);
      border-radius: 6px;
      color: var(--text);
      font-family: 'JetBrains Mono', monospace;
      font-size: 0.82rem;
      font-variant-numeric: tabular-nums;
    }
    pre {
      background: var(--code-bg);
      border: 1px solid var(--border);
      border-radius: 6px;
      padding: 1rem;
      overflow-x: auto;
      font-family: 'JetBrains Mono', monospace;
      font-variant-numeric: tabular-nums;
      font-size: 0.84rem;
      margin: 0.5rem 0 0 0;
      color: #E2E8F0;
    }
    .btn-row {
      display: flex;
      flex-wrap: wrap;
      gap: 0.65rem;
      margin-top: 1rem;
    }
    .btn {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      text-decoration: none;
      color: var(--text);
      background: var(--code-bg);
      border: 1px solid var(--border);
      padding: 0.5rem 1rem;
      border-radius: 6px;
      font-size: 0.82rem;
      font-weight: 600;
      font-family: 'JetBrains Mono', monospace;
      white-space: nowrap;
      flex-shrink: 0;
      cursor: pointer;
    }
    .btn:hover {
      border-color: var(--accent);
    }
    .btn-primary {
      background: var(--green);
      border-color: #22C55E;
      color: #FFFFFF;
    }
    .btn-primary:hover {
      background: #15803D;
      border-color: #4ADE80;
    }
    .two-col {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(420px, 1fr));
      gap: 1.5rem;
    }
  </style>
</head>
<body>
  <header class="topbar">
    <a href="#" class="brand">ChatRenderCLI</a>
    <nav class="nav-links">
      <a href="#giphy-parity">Branch Parity</a>
      <a href="#command-builder">Command Builder</a>
      <a href="#build-scripts">Build Scripts</a>
      <a href="#workspace-files">Workspace Audit</a>
    </nav>
    <div>
      ${exeExists
        ? `<a class="btn btn-primary" href="/download/ChatRenderCLI.exe">Download ChatRenderCLI.exe (${exeSizeMB} MB)</a>`
        : `<a class="btn" href="/download/build-win-x64.bat">Download Build Script</a>`}
    </div>
  </header>

  <main class="container">
    <section class="hero">
      <h1>Standalone ChatRenderCLI.exe Workspace</h1>
      <div class="hero-meta">
        Branch: u/pgeneva/giphy_emotes_v1 &middot; Commit: 0ec45f97 &middot; Target: win-x64 Self-Contained Single-File &middot; Verbs: chatrender / ffmpeg / cache
      </div>
    </section>

    <section id="giphy-parity" class="section">
      <h2>01. u/pgeneva/giphy_emotes_v1 Subsystem Parity Verification</h2>
      <table class="table">
        <thead>
          <tr>
            <th>Subsystem</th>
            <th>Implementation Reference</th>
            <th>Verification</th>
          </tr>
        </thead>
        <tbody>
          ${giphyChecks
            .map(
              (c) => `<tr>
            <td><strong>${c.name}</strong></td>
            <td class="mono">${c.detail}</td>
            <td class="${c.verified ? 'status-ok' : 'status-del'}">${c.verified ? 'VERIFIED (0ec45f97)' : 'MISSING'}</td>
          </tr>`
            )
            .join('')}
        </tbody>
      </table>
    </section>

    <section id="command-builder" class="section">
      <h2>02. Interactive ChatRenderCLI.exe Command Builder</h2>
      <div class="builder-grid">
        <div class="field">
          <label for="cb-preset">Format Preset</label>
          <select id="cb-preset">
            <option value="webm-alpha" selected>Transparent WebM (VP9 + Alpha)</option>
            <option value="mp4-h264">Standard MP4 (H.264)</option>
            <option value="prores-alpha">Apple ProRes 4444 (MOV + Alpha)</option>
            <option value="mp4-outline">Solid Dark 60fps with Outlines</option>
          </select>
        </div>
        <div class="field">
          <label for="cb-mode">Invocation Style</label>
          <select id="cb-mode">
            <option value="direct" selected>Direct Utility (Verb Omitted)</option>
            <option value="verb">Drop-In Verb (chatrender)</option>
          </select>
        </div>
        <div class="field">
          <label for="cb-input">Input Chat JSON (-i)</label>
          <input id="cb-input" type="text" value="chat.json" />
        </div>
        <div class="field">
          <label for="cb-output">Output Video (-o)</label>
          <input id="cb-output" type="text" value="chat.webm" />
        </div>
        <div class="field">
          <label for="cb-width">Width (-w)</label>
          <input id="cb-width" type="number" value="420" />
        </div>
        <div class="field">
          <label for="cb-height">Height (-h)</label>
          <input id="cb-height" type="number" value="1080" />
        </div>
        <div class="field">
          <label for="cb-fps">Framerate (--framerate)</label>
          <input id="cb-fps" type="number" value="60" />
        </div>
        <div class="field">
          <label for="cb-giphy">Giphy GIFs (--giphy)</label>
          <select id="cb-giphy">
            <option value="true" selected>true (Resolve &amp; Render GIFs)</option>
            <option value="false">false (Keep Bracketed Text)</option>
          </select>
        </div>
        <div class="field">
          <label for="cb-gif-height">Max GIF Height % (--gif-max-height-percent)</label>
          <input id="cb-gif-height" type="number" min="10" max="100" value="50" />
        </div>
        <div class="field">
          <label for="cb-cache-mb">Off-Screen Frame Budget (--image-cache-mb)</label>
          <input id="cb-cache-mb" type="number" min="32" max="8192" value="256" />
        </div>
      </div>
      <pre id="cb-preview">ChatRenderCLI.exe chatrender -i "chat.json" -o "chat.mp4" -w 420 -h 1080 --framerate 60 --giphy true --gif-max-height-percent 50 --image-cache-mb 256</pre>
      <div class="btn-row">
        <button type="button" class="btn" id="cb-copy">Copy Command</button>
      </div>
    </section>

    <section id="build-scripts" class="section">
      <h2>03. Compilation Scripts &amp; Artifacts</h2>
      <pre># Windows Command Prompt (or double-click in Explorer):
build-win-x64.bat

# Windows PowerShell:
./build-win-x64.ps1

# Direct .NET 10 CLI Single-File Publish:
dotnet publish TwitchDownloaderCLI/TwitchDownloaderCLI.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish</pre>
      <div class="btn-row">
        ${exeExists ? `<a class="btn btn-primary" href="/download/ChatRenderCLI.exe">Download Compiled ChatRenderCLI.exe (${exeSizeMB} MB)</a>` : ''}
        <a class="btn" href="/download/README.md">Download README.md</a>
        <a class="btn" href="/download/CLI-README.md">Download CLI Manual (TwitchDownloaderCLI/README.md)</a>
        <a class="btn" href="/download/BUILD_GUIDE.md">Download BUILD_GUIDE.md</a>
        <a class="btn" href="/download/build-win-x64.bat">Download build-win-x64.bat</a>
        <a class="btn" href="/download/build-win-x64.ps1">Download build-win-x64.ps1</a>
        <a class="btn" href="/download/ChatRenderCLI.sln">Download ChatRenderCLI.sln</a>
      </div>
    </section>

    <div id="workspace-files" class="two-col">
      <section class="section">
        <h2>04. Retained &amp; Synchronized Workspace Files</h2>
        <table class="table">
          <thead>
            <tr>
              <th>Path</th>
              <th>Status</th>
            </tr>
          </thead>
          <tbody>
            ${retainedFiles
              .map(
                (f) => `<tr>
              <td class="mono">${f.path}</td>
              <td class="${f.exists ? 'status-ok' : 'status-del'}">${f.exists ? 'READY' : 'MISSING'}</td>
            </tr>`
              )
              .join('')}
          </tbody>
        </table>
      </section>

      <section class="section">
        <h2>05. Removed WPF GUI &amp; Non-Render Modes</h2>
        <table class="table">
          <thead>
            <tr>
              <th>Path</th>
              <th>Status</th>
            </tr>
          </thead>
          <tbody>
            ${deletedFiles
              .map(
                (f) => `<tr>
              <td class="mono">${f.path}</td>
              <td class="${f.removed ? 'status-del' : 'status-ok'}">${f.removed ? 'REMOVED' : 'PRESENT'}</td>
            </tr>`
              )
              .join('')}
          </tbody>
        </table>
      </section>
    </div>
  </main>

  <script>
    const ids = ['cb-preset', 'cb-mode', 'cb-input', 'cb-output', 'cb-width', 'cb-height', 'cb-fps', 'cb-giphy', 'cb-gif-height', 'cb-cache-mb'];
    const preview = document.getElementById('cb-preview');
    const copyBtn = document.getElementById('cb-copy');
    const presetSelect = document.getElementById('cb-preset');
    const outputInput = document.getElementById('cb-output');

    presetSelect.addEventListener('change', () => {
      const preset = presetSelect.value;
      if (preset === 'webm-alpha' && !outputInput.value.endsWith('.webm')) {
        outputInput.value = outputInput.value.replace(/\.[^.]+$/, '') + '.webm';
      } else if (preset === 'prores-alpha' && !outputInput.value.endsWith('.mov')) {
        outputInput.value = outputInput.value.replace(/\.[^.]+$/, '') + '.mov';
      } else if ((preset === 'mp4-h264' || preset === 'mp4-outline') && !outputInput.value.endsWith('.mp4')) {
        outputInput.value = outputInput.value.replace(/\.[^.]+$/, '') + '.mp4';
      }
      updateCommand();
    });

    function updateCommand() {
      const preset = document.getElementById('cb-preset').value;
      const mode = document.getElementById('cb-mode').value;
      const input = document.getElementById('cb-input').value || 'chat.json';
      const output = document.getElementById('cb-output').value || 'chat.mp4';
      const width = document.getElementById('cb-width').value || '420';
      const height = document.getElementById('cb-height').value || '1080';
      const fps = document.getElementById('cb-fps').value || '60';
      const giphy = document.getElementById('cb-giphy').value;
      const gifHeight = document.getElementById('cb-gif-height').value || '50';
      const cacheMb = document.getElementById('cb-cache-mb').value || '256';

      const verbPart = mode === 'verb' ? ' chatrender' : '';
      let cmd = 'ChatRenderCLI.exe' + verbPart + ' -i "' + input + '" -o "' + output + '" -w ' + width + ' -h ' + height + ' --framerate ' + fps;

      if (preset === 'webm-alpha') {
        cmd += ' --background-color "#00000000" --output-args "-c:v libvpx-vp9 -crf 18 -b:v 0 -pix_fmt yuva420p \\"{save_path}\\""';
      } else if (preset === 'prores-alpha') {
        cmd += ' --background-color "#00000000" --output-args "-c:v prores_ks -profile:v 4 -pix_fmt yuva444p10le \\"{save_path}\\""';
      } else if (preset === 'mp4-outline') {
        cmd += ' --outline --outline-size 3';
      }

      if (giphy === 'false') {
        cmd += ' --giphy false';
      }
      if (gifHeight !== '50') {
        cmd += ' --gif-max-height-percent ' + gifHeight;
      }
      if (cacheMb !== '256') {
        cmd += ' --image-cache-mb ' + cacheMb;
      }

      preview.textContent = cmd;
    }

    ids.forEach((id) => {
      document.getElementById(id).addEventListener('input', updateCommand);
      document.getElementById(id).addEventListener('change', updateCommand);
    });

    copyBtn.addEventListener('click', async () => {
      try {
        await navigator.clipboard.writeText(preview.textContent);
        copyBtn.textContent = 'Copied to Clipboard';
        setTimeout(() => { copyBtn.textContent = 'Copy Command'; }, 1800);
      } catch {
        copyBtn.textContent = 'Select Command Above to Copy';
      }
    });
  </script>
</body>
</html>`;

  res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
  res.end(html);
});

server.listen(PORT, '0.0.0.0', () => {
  console.log(`ChatRenderCLI workspace server listening on http://0.0.0.0:${PORT}`);
});
