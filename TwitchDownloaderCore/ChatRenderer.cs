using SkiaSharp;
using SkiaSharp.HarfBuzz;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using TwitchDownloaderCore.Chat;
using TwitchDownloaderCore.Extensions;
using TwitchDownloaderCore.Interfaces;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Models.Render;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.Services;
using TwitchDownloaderCore.Tools;
using TwitchDownloaderCore.TwitchObjects;

namespace TwitchDownloaderCore
{
    public sealed partial class ChatRenderer : IDisposable
    {
        private const char ZERO_WIDTH_JOINER = '\u200D';
        public bool Disposed { get; private set; } = false;
        public ChatRoot chatRoot { get; private set; } = new ChatRoot();

        private static readonly SKColor Purple = new(0xFF7B2CF2); // AARRGGBB
        private static readonly SKColor[] DefaultUsernameColors = [new(0xFFFF0000), new(0xFF0000FF), new(0xFF00FF00), new(0xFFB22222), new(0xFFFF7F50), new(0xFF9ACD32), new(0xFFFF4500), new(0xFF2E8B57), new(0xFFDAA520), new(0xFFD2691E), new(0xFF5F9EA0), new(0xFF1E90FF), new(0xFFFF69B4), new(0xFF8A2BE2), new(0xFF00FF7F)];

        private static readonly string[] DefaultAvatarUrls =
        [
            "https://static-cdn.jtvnw.net/user-default-pictures-uv/75305d54-c7cc-40d1-bb9c-91fbe85943c7-profile_image-70x70.png",
            "https://static-cdn.jtvnw.net/user-default-pictures-uv/ebe4cd89-b4f4-4cd9-adac-2f30151b4209-profile_image-70x70.png",
            "https://static-cdn.jtvnw.net/user-default-pictures-uv/215b7342-def9-11e9-9a66-784f43822e80-profile_image-70x70.png",
            "https://static-cdn.jtvnw.net/user-default-pictures-uv/cdd517fe-def4-11e9-948e-784f43822e80-profile_image-70x70.png",
            "https://static-cdn.jtvnw.net/user-default-pictures-uv/41780b5a-def8-11e9-94d9-784f43822e80-profile_image-70x70.png",
            "https://static-cdn.jtvnw.net/user-default-pictures-uv/13e5fa74-defa-11e9-809c-784f43822e80-profile_image-70x70.png",
            "https://static-cdn.jtvnw.net/user-default-pictures-uv/de130ab0-def7-11e9-b668-784f43822e80-profile_image-70x70.png",
            "https://static-cdn.jtvnw.net/user-default-pictures-uv/ead5c8b2-a4c9-4724-b1dd-9f00b46cbd3d-profile_image-70x70.png",
            "https://static-cdn.jtvnw.net/user-default-pictures-uv/ce57700a-def9-11e9-842d-784f43822e80-profile_image-70x70.png",
            "https://static-cdn.jtvnw.net/user-default-pictures-uv/998f01ae-def8-11e9-b95c-784f43822e80-profile_image-70x70.png",
            "https://static-cdn.jtvnw.net/user-default-pictures-uv/dbdc9198-def8-11e9-8681-784f43822e80-profile_image-70x70.png",
            "https://static-cdn.jtvnw.net/user-default-pictures-uv/294c98b5-e34d-42cd-a8f0-140b72fba9b0-profile_image-70x70.png",
        ];

        [GeneratedRegex("[\u0591-\u07FF\uFB1D-\uFDFD\uFE70-\uFEFC]")]
        private static partial Regex RtlRegex { get; }

        [GeneratedRegex("[\u2500-\u257F\u2580-\u259F\u2800-\u28FF]")]
        private static partial Regex BlockArtRegex { get; }

        [GeneratedRegex(@"(?:-cpu-used|-speed)\s+[0-7]\b", RegexOptions.IgnoreCase)]
        private static partial Regex Vp9SlowCpuUsedRegex { get; }

        [GeneratedRegex(@"-threads\s+\d+\b", RegexOptions.IgnoreCase)]
        private static partial Regex FfmpegThreadsRegex { get; }

        private enum PipePixelFormat
        {
            BgraOrRgba,
            Yuv420p,
            Yuva420p
        }

        private static readonly SearchValues<char> DigitChars = SearchValues.Create("0123456789");

        private readonly ITaskProgress _progress;
        private readonly ChatRenderOptions renderOptions;
        private readonly string _cacheDir;
        private DisposableDictionary<string, ChatBadge> _badgeCache = [];
        private DisposableDictionary<string, TwitchEmote> _emoteCache = [];
        private DisposableDictionary<string, TwitchEmote> _emoteThirdCache = [];
        private readonly DisposableDictionary<string, TwitchEmote> _gifCache = new(StringComparer.OrdinalIgnoreCase);
        private DisposableDictionary<string, CheerEmote> _cheermoteCache = [];
        private DisposableDictionary<string, SKImage> _emojiCache = [];
        private Dictionary<string, string> _emojiPaths = [];
        private int _emojiTargetHeight;
        private DisposableDictionary<string, SKImage> _avatarCache = [];
        private DisposableDictionary<int, SKPaint> _fallbackFontCache = [];
        private DisposableDictionary<SKColor, SKPaint> _paintCache = [];
        private readonly SectionImageCache _sectionImageCache = new();
        private readonly ImageMemoryBudget _imageBudget;
        private int _noFallbackFontLogged = 0;
        private readonly SKFontManager fontManager = SKFontManager.CreateDefault();
        private SKPaint messageFont;
        private SKPaint nameFont;
        private SKPaint outlinePaint;
        private readonly HighlightIcons highlightIcons;
        private int _usernameCenteredY;
        private readonly object _progressLock = new();
        private long _lastProgressReportMs = -1000;

        private static readonly ConcurrentDictionary<Process, byte> ActiveFfmpegProcesses = new();

        static ChatRenderer()
        {
            AppDomain.CurrentDomain.ProcessExit += static (_, _) => KillAllActiveFfmpegProcesses();
        }

        private static void RegisterActiveProcess(Process process)
        {
            if (process is not null)
            {
                ActiveFfmpegProcesses.TryAdd(process, 0);
            }
        }

        private static void UnregisterActiveProcess(Process process)
        {
            if (process is not null)
            {
                ActiveFfmpegProcesses.TryRemove(process, out _);
            }
        }

        private static void KillAllActiveFfmpegProcesses()
        {
            foreach (var kvp in ActiveFfmpegProcesses)
            {
                try
                {
                    if (!kvp.Key.HasExited)
                    {
                        kvp.Key.Kill(entireProcessTree: true);
                    }
                }
                catch { /* ignore */ }
            }
            ActiveFfmpegProcesses.Clear();
        }

        // Thread-local rendering contexts for parallel CommentSection and segment rendering
        private ThreadLocal<SectionRenderContext> _workerContexts;

        private sealed class SectionRenderContext : IDisposable
        {
            public required SKPaint MessageFont { get; init; }
            public required SKPaint NameFont { get; init; }
            public required SKPaint OutlinePaint { get; init; }
            public DisposableDictionary<int, SKPaint> FallbackFontCache { get; } = [];
            public DisposableDictionary<SKColor, SKPaint> PaintCache { get; } = [];

            public void Dispose()
            {
                MessageFont?.Dispose();
                NameFont?.Dispose();
                OutlinePaint?.Dispose();
                FallbackFontCache.Dispose();
                PaintCache.Dispose();
            }
        }

        private sealed class RenderWorkerState : IDisposable
        {
            public required int WorkerId { get; init; }
            public required int TotalWorkers { get; init; }
            public required int MaxCommentIndex { get; init; }
            public Dictionary<int, CommentSection> PreRenderedSections { get; } = [];
            public int PreRenderedUpToIndex { get; set; } = -1;
            public HashSet<TwitchEmote> OnScreenEmoteSet { get; } = [];
            public SKBitmap AnimComposedFrame { get; set; }
            public SKCanvas AnimCanvas { get; set; }
            public int AnimByteSize { get; set; }
            public int AnimComposedForCommentIndex { get; set; } = int.MinValue;
            public long AnimComposedVersion { get; set; } = -1;
            public long AnimBaseUpdateVersion { get; set; } = -1;
            public long AnimNextChangeTickMs { get; set; } = -1;
            public long AnimLastTickMs { get; set; } = -1;
            public int AnimDirtyMinY { get; set; } = 0;
            public int AnimDirtyMaxY { get; set; } = 0;
            public long FrameVersionCounter { get; set; } = 0;
            public List<int> AnimLastFrameIndices { get; } = [];

            public void InvalidateAnimCache()
            {
                AnimComposedForCommentIndex = int.MinValue;
                AnimBaseUpdateVersion = -1;
                AnimNextChangeTickMs = -1;
                AnimLastTickMs = -1;
                AnimLastFrameIndices.Clear();
            }

            public void Dispose()
            {
                foreach (var preRendered in PreRenderedSections.Values)
                {
                    preRendered?.Image?.Dispose();
                }
                PreRenderedSections.Clear();
                AnimCanvas?.Dispose();
                AnimComposedFrame?.Dispose();
            }
        }

        private sealed class QueuedFrame
        {
            public required byte[] FrameBuffer { get; init; }
            public required int FrameLength { get; init; }
            public byte[] MaskBuffer { get; init; }
            public int MaskLength { get; init; }
            public int RepeatCount { get; set; } = 1;
            public long Version { get; init; }
        }

        private Dictionary<int, string[]> AllEmojiSequences => field ??=
            _emojiPaths.Keys
                .GroupBy(x => Rune.GetRuneAt(x, 0).Value)
                .ToDictionary(x => x.Key, x => x.ToArray());

        public ChatRenderer(ChatRenderOptions chatRenderOptions, ITaskProgress progress)
        {
            renderOptions = chatRenderOptions;
            _imageBudget = new ImageMemoryBudget(Math.Max(1, renderOptions.ImageCacheMb) * 1024L * 1024L);
            _cacheDir = CacheDirectoryService.GetCacheDirectory(renderOptions.TempFolder);
            renderOptions.BlockArtPreWrapWidth = 29.166 * renderOptions.FontSize - renderOptions.SidePadding * 2;
            renderOptions.BlockArtPreWrap = renderOptions.ChatWidth > renderOptions.BlockArtPreWrapWidth;
            _progress = progress;
            outlinePaint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = (float)(renderOptions.OutlineSize * renderOptions.ReferenceScale), StrokeJoin = SKStrokeJoin.Round, Color = SKColors.Black, IsAntialias = true, IsAutohinted = true, LcdRenderText = true, SubpixelText = true, HintingLevel = SKPaintHinting.Full, FilterQuality = SKFilterQuality.High };
            nameFont = new SKPaint { LcdRenderText = true, SubpixelText = true, TextSize = (float)renderOptions.EffectiveUsernameFontSize, IsAntialias = true, IsAutohinted = true, HintingLevel = SKPaintHinting.Full, FilterQuality = SKFilterQuality.High };
            messageFont = new SKPaint { LcdRenderText = true, SubpixelText = true, TextSize = (float)renderOptions.FontSize, IsAntialias = true, IsAutohinted = true, HintingLevel = SKPaintHinting.Full, FilterQuality = SKFilterQuality.High, Color = renderOptions.MessageColor };
            highlightIcons = new HighlightIcons(renderOptions, _cacheDir, Purple, outlinePaint);
        }

        private SectionRenderContext CreateWorkerContext()
        {
            return new SectionRenderContext
            {
                MessageFont = messageFont.Clone(),
                NameFont = nameFont.Clone(),
                OutlinePaint = outlinePaint.Clone()
            };
        }

        public async Task RenderVideoAsync(CancellationToken cancellationToken)
        {
            var outputFileInfo = TwitchHelper.ClaimFile(renderOptions.OutputFile, renderOptions.FileCollisionCallback, _progress);
            renderOptions.OutputFile = outputFileInfo.FullName;
            var maskFileInfo = renderOptions.GenerateMask ? TwitchHelper.ClaimFile(renderOptions.MaskFile, renderOptions.FileCollisionCallback, _progress) : null;

            // Open the destination files so that they exist in the filesystem.
            await using var outputFs = outputFileInfo.Open(FileMode.Create, FileAccess.Write, FileShare.Read);
            await using var maskFs = maskFileInfo?.Open(FileMode.Create, FileAccess.Write, FileShare.Read);

            try
            {
                await RenderAsyncImpl(outputFileInfo, outputFs, maskFileInfo, maskFs, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);

                TwitchHelper.CleanUpClaimedFile(outputFileInfo, outputFs, _progress);
                TwitchHelper.CleanUpClaimedFile(maskFileInfo, maskFs, _progress);

                throw;
            }
        }

        private async Task RenderAsyncImpl(FileInfo outputFileInfo, FileStream outputFs, FileInfo maskFileInfo, FileStream maskFs, CancellationToken cancellationToken)
        {
            _progress.SetStatus("Fetching Images [1/2]");
            await Task.Run(() => FetchScaledImages(cancellationToken), cancellationToken).ConfigureAwait(false);

            if (renderOptions.DisperseCommentOffsets)
            {
                DisperseCommentOffsets(chatRoot.comments);
            }
            FloorCommentOffsets(chatRoot.comments);

            if (ShouldUseEmbeddedInter(renderOptions.Font))
            {
                nameFont.Typeface = GetInterTypeface(renderOptions.UsernameFontStyle);
                messageFont.Typeface = GetInterTypeface(renderOptions.MessageFontStyle);
            }
            else
            {
                nameFont.Typeface = SKTypeface.FromFamilyName(renderOptions.Font, renderOptions.UsernameFontStyle);
                messageFont.Typeface = SKTypeface.FromFamilyName(renderOptions.Font, renderOptions.MessageFontStyle);
            }

            // Pre-initialize lazy properties on renderOptions and AllEmojiSequences before parallel section workers run
            _ = renderOptions.AlternateBackgroundPaint;
            _ = renderOptions.AlternateBackgroundHighlightUserPaint;
            _ = AllEmojiSequences;
            _workerContexts = new ThreadLocal<SectionRenderContext>(CreateWorkerContext, trackAllValues: true);

            // Cache the rendered timestamp widths
            renderOptions.TimestampWidths = !renderOptions.Timestamp ? Array.Empty<int>() : new[]
            {
                (int)messageFont.MeasureText("0:00"),
                (int)messageFont.MeasureText("00:00"),
                (int)messageFont.MeasureText("0:00:00"),
                (int)messageFont.MeasureText("00:00:00")
            };

            // Cache the username vertical centering offset. The username font may be sized differently from
            // the message font (UsernameFontScale), so it needs to be centered using its own metrics rather
            // than the message-derived sectionDefaultYPos. This is constant for the whole render.
            SKRect usernameBounds = new SKRect();
            nameFont.MeasureText("ABC123", ref usernameBounds);
            _usernameCenteredY = (int)(((renderOptions.SectionHeight - usernameBounds.Height) / 2.0) + usernameBounds.Height);

            // Measure some sample text to determine the text height, cannot assume it is font size
            SKRect sampleTextBounds = new SKRect();
            messageFont.MeasureText("ABC123", ref sampleTextBounds);
            int sectionDefaultYPos = (int)(((renderOptions.SectionHeight - sampleTextBounds.Height) / 2.0) + sampleTextBounds.Height);

            // Rough estimation of the width of a single block art character
            renderOptions.BlockArtCharWidth = GetFallbackFont('█', _fallbackFontCache).MeasureText("█");

            RemoveRestrictedComments(chatRoot.comments);

            (int startTick, int totalTicks) = GetVideoTicks();
            int endTick = startTick + totalTicks;

            // Delete the files as it is not guaranteed that the overwrite flag is passed in the FFmpeg args.
            outputFs.Close();
            outputFileInfo.Refresh();
            if (outputFileInfo.Exists)
                outputFileInfo.Delete();

            maskFs?.Close();
            maskFileInfo?.Refresh();
            if (renderOptions.GenerateMask && maskFileInfo!.Exists)
                maskFileInfo.Delete();

            var segments = ComputeRenderSegments(startTick, endTick);
            _progress.SetTemplateStatus(@"Rendering Video {0}% ({1:h\hm\ms\s} Elapsed | {2:h\hm\ms\s} Remaining)", 0, TimeSpan.Zero, TimeSpan.Zero);

            if (segments.Count <= 1)
            {
                FfmpegProcess ffmpegProcess = GetFfmpegProcess(outputFileInfo, false, 1);
                FfmpegProcess maskProcess = renderOptions.GenerateMask ? GetFfmpegProcess(maskFileInfo, true, 1) : null;
                Stopwatch stopwatch = Stopwatch.StartNew();
                long completedTicks = 0;

                try
                {
                    await Task.Run(() => RenderVideoSegment(0, 1, startTick, endTick, sectionDefaultYPos, totalTicks, ref completedTicks, stopwatch, ffmpegProcess, maskProcess, cancellationToken), cancellationToken).ConfigureAwait(false);
                    stopwatch.Stop();
                    _progress.ReportProgress(100, stopwatch.Elapsed, TimeSpan.Zero);
                    _progress.LogInfo($"FINISHED. RENDER TIME: {stopwatch.Elapsed.TotalSeconds:F1}s SPEED: {totalTicks / (double)renderOptions.Framerate / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds):F2}x");
                }
                catch
                {
                    TerminateAndDisposeProcess(ffmpegProcess, killIfRunning: true);
                    TerminateAndDisposeProcess(maskProcess, killIfRunning: true);
                    GC.Collect();
                    throw;
                }
                finally
                {
                    TerminateAndDisposeProcess(ffmpegProcess, killIfRunning: false);
                    TerminateAndDisposeProcess(maskProcess, killIfRunning: false);
                }

                return;
            }

            await RenderParallelSegmentsAsync(segments, outputFileInfo, maskFileInfo, sectionDefaultYPos, totalTicks, cancellationToken).ConfigureAwait(false);
        }

        private List<(int StartTick, int EndTick)> ComputeRenderSegments(int startTick, int endTick)
        {
            int totalTicks = endTick - startTick;
            if (totalTicks <= 0)
            {
                return [(startTick, endTick)];
            }

            int requestedJobs = renderOptions.ParallelJobs;
            int targetJobs = requestedJobs > 0
                ? Math.Clamp(requestedJobs, 1, 16)
                : Environment.ProcessorCount switch
                {
                    >= 12 => 3,
                    >= 4 => 2,
                    _ => 1
                };

            if (targetJobs <= 1)
            {
                return [(startTick, endTick)];
            }

            int updateStep = Math.Max(1, renderOptions.UpdateFrame);
            int minSegmentTicks = requestedJobs > 1
                ? Math.Max(renderOptions.Framerate, updateStep * 2)
                : Math.Max(renderOptions.Framerate * 10, updateStep * 4);

            int maxPossibleJobs = Math.Max(1, totalTicks / Math.Max(1, minSegmentTicks));
            int jobCount = Math.Min(targetJobs, maxPossibleJobs);
            if (jobCount <= 1)
            {
                return [(startTick, endTick)];
            }

            var segments = new List<(int StartTick, int EndTick)>(jobCount);
            int currentStart = startTick;
            for (int i = 1; i < jobCount; i++)
            {
                int rawBoundary = startTick + (int)((long)totalTicks * i / jobCount);
                int alignedBoundary = (rawBoundary / updateStep) * updateStep;
                if (alignedBoundary <= currentStart)
                {
                    alignedBoundary = ((currentStart / updateStep) + 1) * updateStep;
                }

                if (alignedBoundary < endTick)
                {
                    segments.Add((currentStart, alignedBoundary));
                    currentStart = alignedBoundary;
                }
            }

            if (currentStart < endTick)
            {
                segments.Add((currentStart, endTick));
            }

            return segments;
        }

        private async Task RenderParallelSegmentsAsync(
            List<(int StartTick, int EndTick)> segments,
            FileInfo outputFileInfo,
            FileInfo maskFileInfo,
            int sectionDefaultYPos,
            int totalTicks,
            CancellationToken cancellationToken)
        {
            int workerCount = segments.Count;
            string runId = Guid.NewGuid().ToString("N")[..8];
            string outputDir = outputFileInfo.DirectoryName ?? "";
            string maskDir = maskFileInfo?.DirectoryName ?? outputDir;

            var segmentOutputFiles = new FileInfo[workerCount];
            var segmentMaskFiles = renderOptions.GenerateMask ? new FileInfo[workerCount] : null;
            var ffmpegProcesses = new FfmpegProcess[workerCount];
            var maskProcesses = renderOptions.GenerateMask ? new FfmpegProcess[workerCount] : null;

            Stopwatch stopwatch = Stopwatch.StartNew();
            long completedTicks = 0;

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var workerToken = linkedCts.Token;
            var workerTasks = new Task[workerCount];

            try
            {
                for (int i = 0; i < workerCount; i++)
                {
                    segmentOutputFiles[i] = new FileInfo(Path.Combine(outputDir, $".chatrender_seg_{runId}_{i}{outputFileInfo.Extension}"));
                    if (segmentOutputFiles[i].Exists)
                    {
                        segmentOutputFiles[i].Delete();
                    }
                    ffmpegProcesses[i] = GetFfmpegProcess(segmentOutputFiles[i], false, workerCount);

                    if (renderOptions.GenerateMask)
                    {
                        segmentMaskFiles![i] = new FileInfo(Path.Combine(maskDir, $".chatrender_mask_seg_{runId}_{i}{maskFileInfo!.Extension}"));
                        if (segmentMaskFiles[i].Exists)
                        {
                            segmentMaskFiles[i].Delete();
                        }
                        maskProcesses![i] = GetFfmpegProcess(segmentMaskFiles[i], true, workerCount);
                    }
                }

                for (int i = 0; i < workerCount; i++)
                {
                    int workerId = i;
                    var (segStart, segEnd) = segments[i];
                    var segFfmpeg = ffmpegProcesses[i];
                    var segMask = maskProcesses?[i];

                    workerTasks[i] = Task.Run(
                        () =>
                        {
                            try
                            {
                                RenderVideoSegment(
                                    workerId,
                                    workerCount,
                                    segStart,
                                    segEnd,
                                    sectionDefaultYPos,
                                    totalTicks,
                                    ref completedTicks,
                                    stopwatch,
                                    segFfmpeg,
                                    segMask,
                                    workerToken);
                            }
                            catch
                            {
                                try { linkedCts.Cancel(); } catch { /* ignore */ }
                                throw;
                            }
                        },
                        CancellationToken.None);
                }

                await Task.WhenAll(workerTasks).ConfigureAwait(false);

                // Ensure all segment FFmpeg processes and handles are completely closed and disposed before stitching.
                for (int i = 0; i < workerCount; i++)
                {
                    TerminateAndDisposeProcess(ffmpegProcesses[i], killIfRunning: false);
                    ffmpegProcesses[i] = null;
                    if (maskProcesses != null)
                    {
                        TerminateAndDisposeProcess(maskProcesses[i], killIfRunning: false);
                        maskProcesses[i] = null;
                    }
                }

                _progress.SetStatus("Stitching Video Segments...");
                ConcatSegmentFiles(segmentOutputFiles, outputFileInfo, runId, isMask: false, cancellationToken);
                if (renderOptions.GenerateMask)
                {
                    ConcatSegmentFiles(segmentMaskFiles!, maskFileInfo!, runId, isMask: true, cancellationToken);
                }

                stopwatch.Stop();
                _progress.ReportProgress(100, stopwatch.Elapsed, TimeSpan.Zero);
                _progress.LogInfo($"FINISHED. RENDER TIME: {stopwatch.Elapsed.TotalSeconds:F1}s SPEED: {totalTicks / (double)renderOptions.Framerate / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds):F2}x ({workerCount} parallel workers)");
            }
            catch
            {
                try { linkedCts.Cancel(); } catch { /* ignore */ }
                GC.Collect();
                throw;
            }
            finally
            {
                for (int i = 0; i < workerCount; i++)
                {
                    TerminateAndDisposeProcess(ffmpegProcesses[i], killIfRunning: true);
                    TerminateAndDisposeProcess(maskProcesses?[i], killIfRunning: true);
                }

                try
                {
                    var activeTasks = workerTasks.Where(static t => t is not null).ToArray();
                    if (activeTasks.Length > 0)
                    {
                        await Task.WhenAll(activeTasks).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // Worker exceptions are already propagated by the main try block; wait so no worker thread is still touching segment files.
                }

                for (int i = 0; i < workerCount; i++)
                {
                    DeleteTempFileWithRetry(segmentOutputFiles[i]);
                    if (segmentMaskFiles?[i] != null)
                    {
                        DeleteTempFileWithRetry(segmentMaskFiles[i]);
                    }
                }
            }
        }

        private static void TerminateAndDisposeProcess(FfmpegProcess process, bool killIfRunning)
        {
            if (process is null)
            {
                return;
            }

            UnregisterActiveProcess(process);

            try
            {
                try
                {
                    process.StandardInput?.BaseStream?.Close();
                }
                catch { /* ignore if already closed or disposed */ }

                if (!process.HasExited)
                {
                    if (killIfRunning)
                    {
                        try
                        {
                            process.Kill(entireProcessTree: true);
                        }
                        catch { /* ignore if process exited concurrently */ }
                    }

                    try
                    {
                        process.WaitForExit(5000);
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                            process.WaitForExit(2000);
                        }
                    }
                    catch { /* ignore */ }
                }
            }
            catch { /* ignore if process was never started or already disposed */ }
            finally
            {
                try { process.Dispose(); } catch { /* ignore */ }
            }
        }

        private static void DeleteTempFileWithRetry(FileInfo fileInfo)
        {
            if (fileInfo is null)
            {
                return;
            }

            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    fileInfo.Refresh();
                    if (!fileInfo.Exists)
                    {
                        return;
                    }

                    fileInfo.Delete();
                    return;
                }
                catch (IOException) when (attempt < 9)
                {
                    Thread.Sleep(50);
                }
                catch (UnauthorizedAccessException) when (attempt < 9)
                {
                    Thread.Sleep(50);
                }
                catch
                {
                    return;
                }
            }
        }

        private void ConcatSegmentFiles(IReadOnlyList<FileInfo> segmentFiles, FileInfo finalOutputFile, string runId, bool isMask, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string dir = finalOutputFile.DirectoryName ?? "";
            string listFilePath = Path.Combine(dir, $".chatrender_concat_{(isMask ? "mask_" : "")}{runId}.txt");

            try
            {
                var sb = new StringBuilder();
                foreach (var seg in segmentFiles)
                {
                    seg.Refresh();
                    if (!seg.Exists || seg.Length == 0)
                    {
                        throw new InvalidOperationException($"Parallel render segment file '{seg.FullName}' is missing or empty.");
                    }

                    string escapedName = seg.Name.Replace("'", "'\\''");
                    sb.Append("file '").Append(escapedName).Append("'\n");
                }

                File.WriteAllText(listFilePath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                using var concatProcess = new Process
                {
                    StartInfo =
                    {
                        FileName = renderOptions.FfmpegPath,
                        Arguments = $"-y -hide_banner -loglevel warning -f concat -safe 0 -i \"{listFilePath}\" -c copy \"{finalOutputFile.FullName}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    }
                };

                concatProcess.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                    {
                        _progress.LogFfmpeg(e.Data);
                    }
                };

                _progress.LogVerbose($"Running FFmpeg segment concat with args: {concatProcess.StartInfo.Arguments}");
                concatProcess.Start();
                RegisterActiveProcess(concatProcess);

                try
                {
                    concatProcess.BeginErrorReadLine();
                    concatProcess.BeginOutputReadLine();

                    using var cancelReg = cancellationToken.Register(() =>
                    {
                        try
                        {
                            if (!concatProcess.HasExited)
                            {
                                concatProcess.Kill(entireProcessTree: true);
                            }
                        }
                        catch { /* ignore */ }
                    });

                    while (!concatProcess.WaitForExit(200))
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            try { concatProcess.Kill(entireProcessTree: true); } catch { /* ignore */ }
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                    }

                    concatProcess.WaitForExit();
                    if (concatProcess.ExitCode != 0)
                    {
                        throw new InvalidOperationException($"FFmpeg segment concatenation failed with exit code {concatProcess.ExitCode}.");
                    }
                }
                finally
                {
                    UnregisterActiveProcess(concatProcess);
                    try
                    {
                        if (!concatProcess.HasExited)
                        {
                            concatProcess.Kill(entireProcessTree: true);
                        }
                    }
                    catch { /* ignore */ }
                }
            }
            finally
            {
                DeleteTempFileWithRetry(new FileInfo(listFilePath));
            }
        }

        /// <summary>
        /// Uses the embedded Inter font when "Inter Embedded" is requested, or when "Inter" is requested
        /// on a machine where the Inter font family is not installed in the system font catalog.
        /// </summary>
        private bool ShouldUseEmbeddedInter(string fontName)
        {
            if (string.IsNullOrWhiteSpace(fontName) || fontName.Equals("Inter Embedded", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (fontName.Equals("Inter", StringComparison.OrdinalIgnoreCase))
            {
                using var matched = fontManager.MatchFamily("Inter");
                return matched is null || !matched.FamilyName.Equals("Inter", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        /// <summary>
        /// Due to Twitch changing the API to return only whole number offsets, renders have become less readable.
        /// To get around this we can re-evaluate comment offsets according to their creation date to help restore
        /// the better readability of comments coming in 1-by-1.
        /// </summary>
        private static void DisperseCommentOffsets(List<Comment> comments)
        {
            var commentSpan = CollectionsMarshal.AsSpan(comments);

            // ChatRoot.Video.CreatedAt lacks millisecond accuracy
            var minVideoCreated = DateTime.MaxValue;
            var maxVideoCreated = DateTime.MinValue;
            foreach (var c in commentSpan)
            {
                if (c.content_offset_seconds % 1 != 0)
                {
                    // This is an old chat with millisecond accuracy
                    return;
                }

                var newEstimate = c.created_at - TimeSpan.FromSeconds(c.content_offset_seconds);

                if (newEstimate < minVideoCreated) minVideoCreated = newEstimate;
                if (newEstimate > maxVideoCreated) maxVideoCreated = newEstimate;
            }

            const double MAX_DRIFT_SECONDS = 1.5;
            if ((maxVideoCreated - minVideoCreated).TotalSeconds < MAX_DRIFT_SECONDS)
            {
                foreach (var c in commentSpan)
                {
                    var newOffset = (c.created_at - minVideoCreated).TotalSeconds;
                    Debug.Assert(Math.Abs(newOffset - c.content_offset_seconds) < MAX_DRIFT_SECONDS);
                    c.content_offset_seconds = newOffset;
                }
            }
            else
            {
                // Comments.CreatedAt is inaccurate. Fall back to jitter approximation
                JitterCommentOffsets(comments);
            }
        }

        /// <summary>
        /// Disperse the offsets of comments on each whole second across the second. For example:
        ///   the input a=1.0 b=2.0 c=2.0  d=2.0 e=2.0  f=3.0
        ///     becomes a=1.0 b=2.0 c=2.25 d=2.5 e=2.75 f=3.0
        ///
        /// The only drawback to this method is there will never be multiple comments drawn on the same tick
        /// like in a real chat. The overall improved chat flow is still worth it regardless. */
        /// </summary>
        private static void JitterCommentOffsets(List<Comment> comments)
        {
            var jitterRnd = new Random(comments.Count);

            for (var i = 0; i < comments.Count - 1; i++)
            {
                if (comments[i + 1].content_offset_seconds != comments[i].content_offset_seconds
                    || comments[i].content_offset_seconds % 1 != 0)
                {
                    continue;
                }

                var startIndex = i + 1;
                while (i < comments.Count - 1 && comments[i + 1].content_offset_seconds == comments[i].content_offset_seconds)
                {
                    i++;
                }

                var scaleFactor = 1.0;
                if (i < comments.Count - 1 && comments[i + 1].content_offset_seconds - comments[i].content_offset_seconds < 1)
                {
                    // If there happens to be 2+ comments on a whole second in a chat with decimal comment offsets
                    // we don't want to inadvertently rearrange the comment order
                    scaleFactor = comments[i + 1].content_offset_seconds - comments[i].content_offset_seconds;
                }

                var commentsToUpdate = i - startIndex;
                for (var c = 1; c <= commentsToUpdate; c++) // Start at 1 so we don't offset the first comment on the second
                {
                    var jitter = jitterRnd.NextDouble() * 0.98 - 0.49; // Jitter the distributed comment offset between 0.51-1.49x
                    var distributedOffset = (c + jitter) / (commentsToUpdate + 1); // Jitter must be addition to retain comment order
                    comments[startIndex + c].content_offset_seconds += distributedOffset * scaleFactor;
                }
            }
        }

        /* Why are we doing this? The question is when to display a 0.5 second offset comment with an update rate of 1.
         * At the update frame at 0 seconds, or 1 second? We're choosing at 0 seconds here. Flooring to either the
         * update rate, or if the update rate is greater than 1 just to the next whole number */
        private void FloorCommentOffsets(List<Comment> comments)
        {
            if (renderOptions.UpdateRate <= 0)
                return;

            foreach (var comment in comments)
            {
                if (renderOptions.UpdateRate > 1)
                {
                    comment.content_offset_seconds = Math.Floor(comment.content_offset_seconds);
                }
                else
                {
                    comment.content_offset_seconds = Math.Floor(comment.content_offset_seconds / renderOptions.UpdateRate) * renderOptions.UpdateRate;
                }
            }
        }

        private void RemoveRestrictedComments(List<Comment> comments)
        {
            if (renderOptions.IgnoreUsersArray.Length == 0 && renderOptions.BannedWordsArray.Length == 0)
            {
                return;
            }

            var ignoredUsers = new HashSet<string>(renderOptions.IgnoreUsersArray, StringComparer.InvariantCultureIgnoreCase);

            Regex bannedWordsRegex = null;
            if (renderOptions.BannedWordsArray.Length > 0)
            {
                var bannedWords = string.Join('|', renderOptions.BannedWordsArray.Select(Regex.Escape));
                bannedWordsRegex = new Regex(@$"(?<=\b|[\d\p{{Pc}}]){bannedWords}(?=\b|[\d\p{{Pc}}])",
                    RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }

            for (var i = comments.Count - 1; i >= 0; i--)
            {
                var comment = comments[i];
                var commenter = comment.commenter;

                if (ignoredUsers.Contains(commenter.name) // ASCII login name
                    || (commenter.display_name.Any(IsNotAscii) && ignoredUsers.Contains(commenter.display_name)) // Potentially non-ASCII display name
                    || (bannedWordsRegex is not null && bannedWordsRegex.IsMatch(comment.message.body))) // Banned words
                {
                    comments.RemoveAt(i);
                }
            }
        }

        private static SKTypeface GetInterTypeface(SKFontStyle fontStyle)
        {
            MemoryStream stream = null;
            try
            {
                if (fontStyle == SKFontStyle.Bold)
                    stream = new MemoryStream(Properties.Resources.InterBold);
                else if (fontStyle == SKFontStyle.Italic)
                    stream = new MemoryStream(Properties.Resources.InterItalic);
                else if (fontStyle == SKFontStyle.BoldItalic)
                    stream = new MemoryStream(Properties.Resources.InterBoldItalic);
                else
                    stream = new MemoryStream(Properties.Resources.Inter);

                return SKTypeface.FromStream(stream);
            }
            finally
            {
                stream?.Dispose();
            }
        }

        public static SKBitmap RenderPreviewBitmap(ChatRenderOptions options, int previewWidth, int previewHeight)
        {
            var bitmap = new SKBitmap(previewWidth, previewHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);

            // 1. Draw transparency checkerboard so alpha channel settings are clearly visible
            using (var darkTile = new SKPaint { Color = new SKColor(0xFF1C1C20) })
            using (var lightTile = new SKPaint { Color = new SKColor(0xFF28282E) })
            {
                canvas.Clear(darkTile.Color);
                const int tileSize = 8;
                for (int y = 0; y < previewHeight; y += tileSize)
                {
                    for (int x = ((y / tileSize) & 1) * tileSize; x < previewWidth; x += tileSize * 2)
                    {
                        canvas.DrawRect(x, y, tileSize, tileSize, lightTile);
                    }
                }
            }

            // 2. Blend configured chat background color over checkerboard
            using (var bgPaint = new SKPaint { Color = options.BackgroundColor, BlendMode = SKBlendMode.SrcOver })
            {
                canvas.DrawRect(0, 0, previewWidth, previewHeight, bgPaint);
            }

            bool useEmbeddedInter = string.IsNullOrWhiteSpace(options.Font)
                || options.Font.Equals("Inter Embedded", StringComparison.OrdinalIgnoreCase);
            if (!useEmbeddedInter && options.Font.Equals("Inter", StringComparison.OrdinalIgnoreCase))
            {
                using var fm = SKFontManager.CreateDefault();
                using var matched = fm.MatchFamily("Inter");
                useEmbeddedInter = matched is null || !matched.FamilyName.Equals("Inter", StringComparison.OrdinalIgnoreCase);
            }

            var usernameStyle = options.UsernameFontStyle ?? SKFontStyle.Bold;
            var messageStyle = options.MessageFontStyle ?? SKFontStyle.Normal;
            using var nameTypeface = useEmbeddedInter
                ? GetInterTypeface(usernameStyle)
                : SKTypeface.FromFamilyName(options.Font, usernameStyle) ?? GetInterTypeface(usernameStyle);
            using var msgTypeface = useEmbeddedInter
                ? GetInterTypeface(messageStyle)
                : SKTypeface.FromFamilyName(options.Font, messageStyle) ?? GetInterTypeface(messageStyle);

            double refScale = Math.Max(0.25, options.ReferenceScale);
            using var outlinePaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)Math.Max(0.5, options.OutlineSize * refScale),
                StrokeJoin = SKStrokeJoin.Round,
                Color = SKColors.Black,
                IsAntialias = true,
                IsAutohinted = true,
                LcdRenderText = true,
                SubpixelText = true
            };
            using var nameFont = new SKPaint
            {
                Typeface = nameTypeface,
                TextSize = (float)Math.Max(6, options.EffectiveUsernameFontSize),
                IsAntialias = true,
                IsAutohinted = true,
                LcdRenderText = true,
                SubpixelText = true
            };
            using var msgFont = new SKPaint
            {
                Typeface = msgTypeface,
                TextSize = (float)Math.Max(6, options.FontSize),
                Color = options.MessageColor,
                IsAntialias = true,
                IsAutohinted = true,
                LcdRenderText = true,
                SubpixelText = true
            };

            SKRect sampleBounds = new();
            msgFont.MeasureText("ABC123", ref sampleBounds);
            int sectionHeight = Math.Max(10, options.SectionHeight);
            int textBaselineOffset = (int)(((sectionHeight - sampleBounds.Height) / 2.0) + sampleBounds.Height);

            SKRect userBounds = new();
            nameFont.MeasureText("ABC123", ref userBounds);
            int userBaselineOffset = (int)(((sectionHeight - userBounds.Height) / 2.0) + userBounds.Height);

            int sidePadding = Math.Max(2, options.SidePadding);
            int verticalPadding = Math.Max(2, options.VerticalPadding);
            int wordSpacing = Math.Max(2, options.WordSpacing);

            ReadOnlySpan<(string Time, string User, SKColor Color, string Message, bool IsSub, bool HasEmote)> sampleRows =
            [
                ("1:24", "StreamFan", new SKColor(0xFF1E90FF), "PogChamp nice render!", false, true),
                ("1:25", "Nightbot", new SKColor(0xFF00FF7F), "Welcome to the chat!", false, false),
                ("1:26", "VIP_Viewer", new SKColor(0xFFFF69B4), options.SubMessages ? "subscribed at Tier 1!" : "Let's go!!", options.SubMessages, false)
            ];

            int cursorY = previewHeight;
            for (int i = sampleRows.Length - 1; i >= 0; i--)
            {
                var row = sampleRows[i];
                cursorY -= sectionHeight + verticalPadding;
                if (cursorY < -sectionHeight)
                {
                    break;
                }

                SKColor rowBg = options.BackgroundColor;
                if (options.AlternateMessageBackgrounds && (i & 1) == 1)
                {
                    rowBg = options.AlternateBackgroundColor;
                    using var altPaint = new SKPaint { Color = rowBg, BlendMode = SKBlendMode.SrcOver };
                    canvas.DrawRect(0, cursorY - verticalPadding / 2f, previewWidth, sectionHeight + verticalPadding, altPaint);
                }

                int drawX = sidePadding;
                if (row.IsSub)
                {
                    using var subBgPaint = new SKPaint { Color = new SKColor(0x286B6B6E) };
                    canvas.DrawRect(sidePadding, cursorY, previewWidth - sidePadding * 2, sectionHeight, subBgPaint);
                    using var accentPaint = new SKPaint { Color = Purple };
                    canvas.DrawRect(sidePadding, cursorY, Math.Max(2, options.AccentStrokeWidth), sectionHeight, accentPaint);
                    drawX += Math.Max(6, options.AccentIndentWidth);
                }

                int msgY = cursorY + textBaselineOffset;
                int userY = cursorY + userBaselineOffset;

                if (options.Timestamp && !row.IsSub)
                {
                    DrawPreviewString(canvas, row.Time, drawX, msgY, msgFont, outlinePaint, options.Outline);
                    drawX += (int)msgFont.MeasureText(row.Time) + wordSpacing * 2;
                }

                if (options.RenderUserAvatars && !row.IsSub)
                {
                    int avatarDiameter = Math.Max(8, (int)Math.Round(36 * refScale * options.AvatarScale));
                    float avatarRadius = avatarDiameter / 2f;
                    float avatarCenterY = cursorY + sectionHeight / 2f;
                    using var avatarPaint = new SKPaint { Color = row.Color, IsAntialias = true };
                    canvas.DrawCircle(drawX + avatarRadius, avatarCenterY, avatarRadius, avatarPaint);
                    drawX += avatarDiameter + wordSpacing;
                }

                if (options.ChatBadges && !row.IsSub)
                {
                    int badgeSize = Math.Max(6, (int)Math.Round(36 * refScale * options.BadgeScale));
                    float badgeTop = cursorY + (sectionHeight - badgeSize) / 2f;
                    using var badgePaint = new SKPaint { Color = i == 0 ? Purple : new SKColor(0xFF00AD03), IsAntialias = true };
                    canvas.DrawRoundRect(drawX, badgeTop, badgeSize, badgeSize, 3f, 3f, badgePaint);
                    drawX += badgeSize + Math.Max(1, wordSpacing / 2);
                }

                SKColor userColor = row.Color;
                if (options.AdjustUsernameVisibility && (options.Outline || rowBg.Alpha >= 128))
                {
                    userColor = AdjustColorVisibility(userColor, options.Outline ? SKColors.Black : rowBg);
                }
                nameFont.Color = userColor;

                string userLabel = row.IsSub ? row.User : row.User + ":";
                DrawPreviewString(canvas, userLabel, drawX, userY, nameFont, outlinePaint, options.Outline);
                drawX += (int)nameFont.MeasureText(userLabel) + wordSpacing;

                if (row.HasEmote)
                {
                    int emoteSize = Math.Max(8, (int)Math.Round(28 * refScale * options.EmoteScale));
                    float emoteTop = cursorY + (sectionHeight - emoteSize) / 2f;
                    using var emotePaint = new SKPaint { Color = new SKColor(0xFFFFB800), IsAntialias = true };
                    canvas.DrawCircle(drawX + emoteSize / 2f, emoteTop + emoteSize / 2f, emoteSize / 2f, emotePaint);
                    drawX += emoteSize + Math.Max(2, options.EmoteSpacing);
                    DrawPreviewString(canvas, "nice render!", drawX, msgY, msgFont, outlinePaint, options.Outline);
                }
                else
                {
                    DrawPreviewString(canvas, row.Message, drawX, msgY, msgFont, outlinePaint, options.Outline);
                }
            }

            return bitmap;

            static void DrawPreviewString(SKCanvas c, string text, int x, int y, SKPaint font, SKPaint outline, bool drawOutline)
            {
                if (drawOutline)
                {
                    using var path = font.GetTextPath(text, x, y);
                    c.DrawPath(path, outline);
                }
                c.DrawText(text, x, y, font);
            }
        }

        private void RenderVideoSegment(
            int workerId,
            int totalWorkers,
            int startTick,
            int endTick,
            int sectionDefaultYPos,
            int totalRenderTicks,
            ref long completedTicks,
            Stopwatch stopwatch,
            FfmpegProcess ffmpegProcess,
            FfmpegProcess maskProcess,
            CancellationToken cancellationToken)
        {
            var ffmpegStream = ffmpegProcess.StandardInput.BaseStream;
            var maskStream = maskProcess?.StandardInput.BaseStream;

            DriveInfo outputDrive = DriveHelper.GetOutputDrive(ffmpegProcess.SavePath);

            var pipeFormat = GetPipePixelFormat();
            var pixelCount = renderOptions.ChatWidth * renderOptions.ChatHeight;
            var frameByteLength = pipeFormat switch
            {
                PipePixelFormat.Yuva420p => pixelCount * 5 / 2,
                PipePixelFormat.Yuv420p => pixelCount * 3 / 2,
                _ => pixelCount * 4
            };
            var maskByteLength = pixelCount;
            var hasMask = maskProcess != null;
            var isBgra = SKImageInfo.PlatformColorType == SKColorType.Bgra8888;

            var maxSegmentTimeSeconds = Math.Max(0, (endTick - 1) / (double)renderOptions.Framerate);
            var maxCommentIdx = chatRoot.comments.Count == 0
                ? -1
                : GetNewestCommentIndex(-1, maxSegmentTimeSeconds);

            using var workerState = new RenderWorkerState
            {
                WorkerId = workerId,
                TotalWorkers = totalWorkers,
                MaxCommentIndex = maxCommentIdx
            };

            var frameChannel = Channel.CreateBounded<QueuedFrame>(new BoundedChannelOptions(32)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });

            using var cancelReg = cancellationToken.Register(() =>
            {
                try { if (!ffmpegProcess.HasExited) ffmpegProcess.Kill(entireProcessTree: true); } catch { /* ignore */ }
                try { if (maskProcess is { HasExited: false }) maskProcess.Kill(entireProcessTree: true); } catch { /* ignore */ }
            });

            var pipeWriterTask = Task.Run(async () =>
            {
                await foreach (var item in frameChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    try
                    {
                        for (int r = 0; r < item.RepeatCount; r++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            if (!renderOptions.SkipDriveWaiting)
                            {
                                DriveHelper.WaitForDrive(outputDrive, _progress);
                            }

                            ffmpegStream.Write(item.FrameBuffer, 0, item.FrameLength);

                            if (hasMask && item.MaskBuffer != null)
                            {
                                if (!renderOptions.SkipDriveWaiting)
                                {
                                    DriveHelper.WaitForDrive(outputDrive, _progress);
                                }

                                maskStream.Write(item.MaskBuffer, 0, item.MaskLength);
                            }
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(item.FrameBuffer);
                        if (item.MaskBuffer != null)
                        {
                            ArrayPool<byte>.Shared.Return(item.MaskBuffer);
                        }
                    }
                }
            }, cancellationToken);

            UpdateFrame latestUpdate = null;
            QueuedFrame pendingFrame = null;
            byte[] cachedUpdateYuvBuffer = pipeFormat != PipePixelFormat.BgraOrRgba
                ? ArrayPool<byte>.Shared.Rent(frameByteLength)
                : null;
            byte[] cachedAnimYuvBuffer = pipeFormat != PipePixelFormat.BgraOrRgba
                ? ArrayPool<byte>.Shared.Rent(frameByteLength)
                : null;
            long cachedUpdateYuvVersion = -1;
            long cachedAnimBaseUpdateVersion = -1;

            try
            {
                for (int currentTick = startTick; currentTick < endTick; currentTick++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (latestUpdate == null || currentTick % renderOptions.UpdateFrame == 0)
                    {
                        latestUpdate = GenerateUpdateFrame(workerState, currentTick, sectionDefaultYPos, latestUpdate);
                    }

                    var (frame, version) = GetFrameFromTickWithVersion(workerState, currentTick, sectionDefaultYPos, latestUpdate);

                    // Coalesce identical consecutive frames without copying pixel memory, recomputing YUVA420P planes, or recomputing the alpha mask
                    if (pendingFrame != null && pendingFrame.Version == version)
                    {
                        pendingFrame.RepeatCount++;
                    }
                    else
                    {
                        if (pendingFrame != null)
                        {
                            EnqueueFrameSync(frameChannel.Writer, pendingFrame, cancellationToken);
                        }

                        var frameSpan = frame.GetPixelSpan();
                        var rentedFrame = ArrayPool<byte>.Shared.Rent(frameByteLength);

                        if (pipeFormat == PipePixelFormat.BgraOrRgba)
                        {
                            frameSpan.CopyTo(rentedFrame);
                        }
                        else
                        {
                            var includeAlpha = pipeFormat == PipePixelFormat.Yuva420p;
                            if (latestUpdate != null && cachedUpdateYuvVersion != latestUpdate.Version)
                            {
                                ConvertFrameToYuv420p(
                                    latestUpdate.Image.GetPixelSpan(),
                                    cachedUpdateYuvBuffer.AsSpan(0, frameByteLength),
                                    renderOptions.ChatWidth,
                                    renderOptions.ChatHeight,
                                    0,
                                    renderOptions.ChatHeight,
                                    includeAlpha,
                                    isBgra);
                                cachedUpdateYuvVersion = latestUpdate.Version;
                            }

                            if (latestUpdate != null && ReferenceEquals(frame, latestUpdate.Image))
                            {
                                Buffer.BlockCopy(cachedUpdateYuvBuffer, 0, rentedFrame, 0, frameByteLength);
                            }
                            else if (latestUpdate != null && ReferenceEquals(frame, workerState.AnimComposedFrame) && workerState.AnimDirtyMaxY > workerState.AnimDirtyMinY)
                            {
                                if (cachedAnimBaseUpdateVersion != latestUpdate.Version)
                                {
                                    Buffer.BlockCopy(cachedUpdateYuvBuffer, 0, cachedAnimYuvBuffer, 0, frameByteLength);
                                    cachedAnimBaseUpdateVersion = latestUpdate.Version;
                                }

                                ConvertFrameToYuv420p(
                                    frameSpan,
                                    cachedAnimYuvBuffer.AsSpan(0, frameByteLength),
                                    renderOptions.ChatWidth,
                                    renderOptions.ChatHeight,
                                    workerState.AnimDirtyMinY,
                                    workerState.AnimDirtyMaxY,
                                    includeAlpha,
                                    isBgra);
                                Buffer.BlockCopy(cachedAnimYuvBuffer, 0, rentedFrame, 0, frameByteLength);
                            }
                            else
                            {
                                ConvertFrameToYuv420p(
                                    frameSpan,
                                    rentedFrame.AsSpan(0, frameByteLength),
                                    renderOptions.ChatWidth,
                                    renderOptions.ChatHeight,
                                    0,
                                    renderOptions.ChatHeight,
                                    includeAlpha,
                                    isBgra);
                            }
                        }

                        byte[] rentedMask = null;
                        if (hasMask)
                        {
                            rentedMask = ArrayPool<byte>.Shared.Rent(maskByteLength);
                            if (pipeFormat == PipePixelFormat.Yuva420p)
                            {
                                rentedFrame.AsSpan(pixelCount * 3 / 2, maskByteLength).CopyTo(rentedMask);
                            }
                            else
                            {
                                SetFrameMask(frameSpan, rentedMask.AsSpan(0, maskByteLength));
                            }
                        }

                        pendingFrame = new QueuedFrame
                        {
                            FrameBuffer = rentedFrame,
                            FrameLength = frameByteLength,
                            MaskBuffer = rentedMask,
                            MaskLength = maskByteLength,
                            RepeatCount = 1,
                            Version = version
                        };
                    }

                    long doneTicks = Interlocked.Increment(ref completedTicks);
                    if (doneTicks % (totalWorkers > 1 ? 12 : 3) == 0 && totalRenderTicks > 0)
                    {
                        long elapsedMs = stopwatch.ElapsedMilliseconds;
                        long prevReportMs = Volatile.Read(ref _lastProgressReportMs);
                        if (elapsedMs - prevReportMs >= 100 || doneTicks >= totalRenderTicks)
                        {
                            Interlocked.Exchange(ref _lastProgressReportMs, elapsedMs);
                            var percent = doneTicks / (double)totalRenderTicks * 100;
                            if (percent > 0)
                            {
                                var elapsedSeconds = elapsedMs / 1000.0;
                                var secondsLeft = unchecked((int)(100 / percent * elapsedSeconds - elapsedSeconds));
                                lock (_progressLock)
                                {
                                    _progress.ReportProgress(
                                        (int)Math.Round(percent),
                                        TimeSpan.FromSeconds((long)elapsedSeconds),
                                        TimeSpan.FromSeconds(Math.Max(0, secondsLeft)));
                                }
                            }
                        }
                    }
                }

                if (pendingFrame != null)
                {
                    EnqueueFrameSync(frameChannel.Writer, pendingFrame, cancellationToken);
                    pendingFrame = null;
                }

                frameChannel.Writer.Complete();
                pipeWriterTask.GetAwaiter().GetResult();

                ffmpegStream.Dispose();
                maskStream?.Dispose();

                WaitForFfmpegExit(ffmpegProcess, cancellationToken);
                if (maskProcess != null)
                {
                    WaitForFfmpegExit(maskProcess, cancellationToken);
                }

                if (ffmpegProcess.HasExited && ffmpegProcess.ExitCode != 0)
                {
                    throw new InvalidOperationException($"FFmpeg process for segment {workerId} exited with code {ffmpegProcess.ExitCode}.");
                }
                if (maskProcess != null && maskProcess.HasExited && maskProcess.ExitCode != 0)
                {
                    throw new InvalidOperationException($"FFmpeg mask process for segment {workerId} exited with code {maskProcess.ExitCode}.");
                }
            }
            catch
            {
                if (pendingFrame != null)
                {
                    ArrayPool<byte>.Shared.Return(pendingFrame.FrameBuffer);
                    if (pendingFrame.MaskBuffer != null)
                    {
                        ArrayPool<byte>.Shared.Return(pendingFrame.MaskBuffer);
                    }
                }
                frameChannel.Writer.TryComplete();
                try { if (!ffmpegProcess.HasExited) ffmpegProcess.Kill(entireProcessTree: true); } catch { /* ignore */ }
                try { if (maskProcess is { HasExited: false }) maskProcess.Kill(entireProcessTree: true); } catch { /* ignore */ }
                try { pipeWriterTask.GetAwaiter().GetResult(); } catch { /* propagate primary exception */ }
                throw;
            }
            finally
            {
                if (cachedUpdateYuvBuffer != null)
                {
                    ArrayPool<byte>.Shared.Return(cachedUpdateYuvBuffer);
                }
                if (cachedAnimYuvBuffer != null)
                {
                    ArrayPool<byte>.Shared.Return(cachedAnimYuvBuffer);
                }

                try { ffmpegStream?.Dispose(); } catch { /* ignore */ }
                try { maskStream?.Dispose(); } catch { /* ignore */ }

                latestUpdate?.Canvas?.Dispose();
                latestUpdate?.Image?.Dispose();
                latestUpdate?.Comments?.ForEach(x => x.Image.Dispose());
                _imageBudget.UnregisterWorker(workerId);
            }
        }

        private static void WaitForFfmpegExit(FfmpegProcess process, CancellationToken cancellationToken)
        {
            if (process is null)
            {
                return;
            }

            const int pollMs = 200;
            const int maxWaitMs = 120_000;
            int waitedMs = 0;

            while (!process.WaitForExit(pollMs))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                    cancellationToken.ThrowIfCancellationRequested();
                }

                waitedMs += pollMs;
                if (waitedMs >= maxWaitMs)
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                    throw new TimeoutException($"FFmpeg process did not exit within {maxWaitMs / 1000}s after closing input stream.");
                }
            }

            // Ensure asynchronous stdout/stderr reader callbacks have flushed.
            process.WaitForExit();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EnqueueFrameSync(ChannelWriter<QueuedFrame> writer, QueuedFrame frame, CancellationToken cancellationToken)
        {
            if (!writer.TryWrite(frame))
            {
                writer.WriteAsync(frame, cancellationToken).AsTask().GetAwaiter().GetResult();
            }
        }

        private static unsafe void SetFrameMask(ReadOnlySpan<byte> frame, Span<byte> maskBytes)
        {
            Debug.Assert(frame.Length == maskBytes.Length * 4); // 32bpp -> 8bpp

            var produced = 0;
            var outCount = maskBytes.Length;

            fixed (byte* pFrame = frame)
            fixed (byte* pMask = maskBytes)
            {
                if (Avx512Vbmi.IsSupported && Avx512F.IsSupported)
                {
                    // Take every 4th byte across 64 bytes (16 RGBA/BGRA pixels -> 16 alpha bytes) in a single AVX-512 VBMI instruction
                    var permuteIdx = Vector512.Create(
                        (byte)3, 7, 11, 15, 19, 23, 27, 31, 35, 39, 43, 47, 51, 55, 59, 63,
                        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
                    );

                    for (; produced + 16 <= outCount; produced += 16)
                    {
                        var vec = Avx512F.LoadVector512(pFrame + produced * 4);
                        var permuted = Avx512Vbmi.PermuteVar64x8(vec, permuteIdx);
                        Sse2.Store(pMask + produced, permuted.GetLower().GetLower());
                    }
                }
                else if (Avx2.IsSupported)
                {
                    // Take every 4th byte. AVX2 shuffles each 128-bit lane independently
                    var shuffleMask = Vector256.Create(
                        3, 7, 11, 15, 0x80, 0x80, 0x80, 0x80,
                        0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80,
                        3, 7, 11, 15, 0x80, 0x80, 0x80, 0x80,
                        0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80
                    );

                    for (; produced + 8 <= outCount; produced += 8)
                    {
                        var vec = Avx.LoadVector256(pFrame + produced * 4);
                        var shuffle = Avx2.Shuffle(vec, shuffleMask);

                        var lo = shuffle.GetLower().AsUInt32().ToScalar();
                        var hi = shuffle.GetUpper().AsUInt32().ToScalar();
                        *(ulong*)(pMask + produced) = lo | ((ulong)hi << 32);
                    }
                }
                else if (Ssse3.IsSupported)
                {
                    // Take every 4th byte
                    var shuffleMask = Vector128.Create(
                        3, 7, 11, 15,
                        0x80, 0x80, 0x80, 0x80,
                        0x80, 0x80, 0x80, 0x80,
                        0x80, 0x80, 0x80, 0x80
                    );

                    for (; produced + 4 <= outCount; produced += 4)
                    {
                        var vec = Sse2.LoadVector128(pFrame + produced * 4);
                        var shuffle = Ssse3.Shuffle(vec, shuffleMask);

                        *(uint*)(pMask + produced) = shuffle.AsUInt32().ToScalar();
                    }
                }
                else if (AdvSimd.Arm64.IsSupported)
                {
                    // Take every 4th byte
                    var tableIdx = Vector128.Create(
                        3, 7, 11, 15,
                        0xFF, 0xFF, 0xFF, 0xFF,
                        0xFF, 0xFF, 0xFF, 0xFF,
                        0xFF, 0xFF, 0xFF, 0xFF
                    );

                    for (; produced + 4 <= outCount; produced += 4)
                    {
                        var vec = AdvSimd.LoadVector128(pFrame + produced * 4);
                        var shuffle = AdvSimd.Arm64.VectorTableLookup(vec, tableIdx);

                        *(uint*)(pMask + produced) = shuffle.AsUInt32().ToScalar();
                    }
                }

                // Scalar fallback for when SIMD is unavailable/finish copy if vector size % outCount != 0
                var pF = pFrame + produced * 4 + 3;
                var pM = pMask + produced;
                var frameEnd = pFrame + frame.Length;
                for (; pF < frameEnd; pF += 4)
                {
                    *pM++ = *pF;
                }
            }
        }

        private PipePixelFormat GetPipePixelFormat()
        {
            if (renderOptions.ChatWidth <= 0
                || renderOptions.ChatHeight <= 0
                || (renderOptions.ChatWidth & 1) != 0
                || (renderOptions.ChatHeight & 1) != 0)
            {
                return PipePixelFormat.BgraOrRgba;
            }

            var inputArgs = renderOptions.InputArgs ?? string.Empty;
            var outputArgs = renderOptions.OutputArgs ?? string.Empty;

            if (!inputArgs.Contains("{pix_fmt}", StringComparison.Ordinal)
                || inputArgs.Contains("-vf ", StringComparison.OrdinalIgnoreCase)
                || outputArgs.Contains("-vf ", StringComparison.OrdinalIgnoreCase)
                || inputArgs.Contains("-filter_complex", StringComparison.OrdinalIgnoreCase)
                || outputArgs.Contains("-filter_complex", StringComparison.OrdinalIgnoreCase))
            {
                return PipePixelFormat.BgraOrRgba;
            }

            if (outputArgs.Contains("-pix_fmt yuva420p", StringComparison.OrdinalIgnoreCase))
            {
                return PipePixelFormat.Yuva420p;
            }

            if (outputArgs.Contains("-pix_fmt yuv420p", StringComparison.OrdinalIgnoreCase))
            {
                return PipePixelFormat.Yuv420p;
            }

            return PipePixelFormat.BgraOrRgba;
        }

        /// <summary>
        /// Converts a 32-bpp BGRA/RGBA frame into planar BT.601 YUV420P or YUVA420P once per unique frame version,
        /// bypassing redundant per-tick FFmpeg swscale conversions on repeated frames and reducing rawvideo pipe
        /// bandwidth by 37.5% (yuva420p) to 62.5% (yuv420p). Uses a 64-bit uniform 2x2 block fast-path that
        /// converts flat chat background and solid regions without per-pixel multiplications.
        /// </summary>
        private static unsafe void ConvertFrameToYuv420p(
            ReadOnlySpan<byte> srcFrame,
            Span<byte> dstPlanes,
            int width,
            int height,
            int startY,
            int endY,
            bool includeAlpha,
            bool isBgra)
        {
            var pixelCount = width * height;
            var uvWidth = width >> 1;
            var uvSize = uvWidth * (height >> 1);

            fixed (byte* pSrc = srcFrame)
            fixed (byte* pDst = dstPlanes)
            {
                byte* pYPlane = pDst;
                byte* pUPlane = pDst + pixelCount;
                byte* pVPlane = pUPlane + uvSize;
                byte* pAPlane = includeAlpha ? pVPlane + uvSize : null;

                // Seed the uniform 2x2 block cache with the first pixel (typically the chat background color)
                uint cachedPixel = *(uint*)pSrc;
                ulong cachedPair = cachedPixel | ((ulong)cachedPixel << 32);
                ComputeUniformBlockYuv(cachedPixel, isBgra, out ushort cachedY2, out ushort cachedA2, out byte cachedU, out byte cachedV);

                int strideBytes = width * 4;

                for (int y = startY; y < endY; y += 2)
                {
                    byte* pRow0 = pSrc + y * strideBytes;
                    byte* pRow1 = pRow0 + strideBytes;

                    byte* pY0 = pYPlane + y * width;
                    byte* pY1 = pY0 + width;

                    int uvRowOffset = (y >> 1) * uvWidth;
                    byte* pU = pUPlane + uvRowOffset;
                    byte* pV = pVPlane + uvRowOffset;

                    byte* pA0 = includeAlpha ? pAPlane + y * width : null;
                    byte* pA1 = includeAlpha ? pA0 + width : null;

                    for (int x = 0; x < width; x += 2)
                    {
                        ulong pair0 = *(ulong*)(pRow0 + x * 4);
                        ulong pair1 = *(ulong*)(pRow1 + x * 4);

                        if (((pair0 ^ cachedPair) | (pair1 ^ cachedPair)) == 0)
                        {
                            *(ushort*)(pY0 + x) = cachedY2;
                            *(ushort*)(pY1 + x) = cachedY2;
                            int uvIdx = x >> 1;
                            pU[uvIdx] = cachedU;
                            pV[uvIdx] = cachedV;
                            if (includeAlpha)
                            {
                                *(ushort*)(pA0 + x) = cachedA2;
                                *(ushort*)(pA1 + x) = cachedA2;
                            }
                            continue;
                        }

                        uint p00 = (uint)pair0;
                        uint p01 = (uint)(pair0 >> 32);
                        if (pair0 == pair1 && p00 == p01)
                        {
                            cachedPixel = p00;
                            cachedPair = pair0;
                            ComputeUniformBlockYuv(p00, isBgra, out cachedY2, out cachedA2, out cachedU, out cachedV);

                            *(ushort*)(pY0 + x) = cachedY2;
                            *(ushort*)(pY1 + x) = cachedY2;
                            int uvIdx = x >> 1;
                            pU[uvIdx] = cachedU;
                            pV[uvIdx] = cachedV;
                            if (includeAlpha)
                            {
                                *(ushort*)(pA0 + x) = cachedA2;
                                *(ushort*)(pA1 + x) = cachedA2;
                            }
                            continue;
                        }

                        uint p10 = (uint)pair1;
                        uint p11 = (uint)(pair1 >> 32);

                        int r00, g00 = (int)((p00 >> 8) & 0xFF), b00;
                        int r01, g01 = (int)((p01 >> 8) & 0xFF), b01;
                        int r10, g10 = (int)((p10 >> 8) & 0xFF), b10;
                        int r11, g11 = (int)((p11 >> 8) & 0xFF), b11;

                        if (isBgra)
                        {
                            b00 = (int)(p00 & 0xFF); r00 = (int)((p00 >> 16) & 0xFF);
                            b01 = (int)(p01 & 0xFF); r01 = (int)((p01 >> 16) & 0xFF);
                            b10 = (int)(p10 & 0xFF); r10 = (int)((p10 >> 16) & 0xFF);
                            b11 = (int)(p11 & 0xFF); r11 = (int)((p11 >> 16) & 0xFF);
                        }
                        else
                        {
                            r00 = (int)(p00 & 0xFF); b00 = (int)((p00 >> 16) & 0xFF);
                            r01 = (int)(p01 & 0xFF); b01 = (int)((p01 >> 16) & 0xFF);
                            r10 = (int)(p10 & 0xFF); b10 = (int)((p10 >> 16) & 0xFF);
                            r11 = (int)(p11 & 0xFF); b11 = (int)((p11 >> 16) & 0xFF);
                        }

                        int y00 = ((66 * r00 + 129 * g00 + 25 * b00 + 128) >> 8) + 16;
                        int y01 = ((66 * r01 + 129 * g01 + 25 * b01 + 128) >> 8) + 16;
                        int y10 = ((66 * r10 + 129 * g10 + 25 * b10 + 128) >> 8) + 16;
                        int y11 = ((66 * r11 + 129 * g11 + 25 * b11 + 128) >> 8) + 16;

                        *(ushort*)(pY0 + x) = (ushort)(y00 | (y01 << 8));
                        *(ushort*)(pY1 + x) = (ushort)(y10 | (y11 << 8));

                        int sumR = r00 + r01 + r10 + r11;
                        int sumG = g00 + g01 + g10 + g11;
                        int sumB = b00 + b01 + b10 + b11;

                        int uvOffset = x >> 1;
                        pU[uvOffset] = (byte)(((-38 * sumR - 74 * sumG + 112 * sumB + 512) >> 10) + 128);
                        pV[uvOffset] = (byte)(((112 * sumR - 94 * sumG - 18 * sumB + 512) >> 10) + 128);

                        if (includeAlpha)
                        {
                            *(ushort*)(pA0 + x) = (ushort)((p00 >> 24) | ((p01 >> 16) & 0xFF00));
                            *(ushort*)(pA1 + x) = (ushort)((p10 >> 24) | ((p11 >> 16) & 0xFF00));
                        }
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ComputeUniformBlockYuv(uint pixel, bool isBgra, out ushort y2, out ushort a2, out byte u, out byte v)
        {
            int g = (int)((pixel >> 8) & 0xFF);
            int r, b;
            if (isBgra)
            {
                b = (int)(pixel & 0xFF);
                r = (int)((pixel >> 16) & 0xFF);
            }
            else
            {
                r = (int)(pixel & 0xFF);
                b = (int)((pixel >> 16) & 0xFF);
            }

            uint a = pixel >> 24;
            int yVal = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
            y2 = (ushort)(yVal | (yVal << 8));
            a2 = (ushort)(a | (a << 8));

            int sumR = r << 2;
            int sumG = g << 2;
            int sumB = b << 2;
            u = (byte)(((-38 * sumR - 74 * sumG + 112 * sumB + 512) >> 10) + 128);
            v = (byte)(((112 * sumR - 94 * sumG - 18 * sumB + 512) >> 10) + 128);
        }

        private FfmpegProcess GetFfmpegProcess(FileInfo fileInfo, bool isMask = false, int parallelJobs = 1)
        {
            string savePath = fileInfo.FullName;
            var pixFmt = isMask
                ? "gray"
                : GetPipePixelFormat() switch
                {
                    PipePixelFormat.Yuva420p => "yuva420p",
                    PipePixelFormat.Yuv420p => "yuv420p",
                    _ => SKImageInfo.PlatformColorType == SKColorType.Bgra8888 ? "bgra" : "rgba"
                };

            string inputArgs = new StringBuilder(renderOptions.InputArgs)
                .Replace("-analyzeduration {max_int} -probesize {max_int}", "-analyzeduration 0 -probesize 32 -thread_queue_size 1024")
                .Replace("{fps}", renderOptions.Framerate.ToString())
                .Replace("{height}", renderOptions.ChatHeight.ToString())
                .Replace("{width}", renderOptions.ChatWidth.ToString())
                .Replace("{save_path}", savePath)
                .Replace("{max_int}", int.MaxValue.ToString())
                .Replace("{pix_fmt}", pixFmt)
                .ToString();

            string rawOutputArgs = OptimizeFfmpegOutputArgs(renderOptions.OutputArgs, renderOptions.Framerate, parallelJobs);
            string outputArgs = new StringBuilder(rawOutputArgs)
                .Replace("{fps}", renderOptions.Framerate.ToString())
                .Replace("{height}", renderOptions.ChatHeight.ToString())
                .Replace("{width}", renderOptions.ChatWidth.ToString())
                .Replace("{save_path}", savePath)
                .Replace("{max_int}", int.MaxValue.ToString())
                .ToString();

            var process = new FfmpegProcess
            {
                StartInfo =
                {
                    FileName = renderOptions.FfmpegPath,
                    Arguments = $"{inputArgs} {outputArgs}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
                SavePath = savePath
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    _progress.LogFfmpeg(e.Data);
                }
            };

            _progress.LogVerbose($"Running \"{renderOptions.FfmpegPath}\" in \"{process.StartInfo.WorkingDirectory}\" with args: {process.StartInfo.Arguments}");

            process.Start();
            RegisterActiveProcess(process);
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            return process;
        }

        /// <summary>
        /// Optimizes libvpx-vp9 realtime FFmpeg output flags for high-core-count multi-threaded alpha encoding:
        /// switches from slow RD/realtime modes (-cpu-used 0..7) to fastest Non-RD screen/overlay realtime mode (-cpu-used 8),
        /// disables lookahead buffering (-lag-in-frames 0), enables row/tile/frame parallelism, and sets explicit thread scaling.
        /// </summary>
        private static string OptimizeFfmpegOutputArgs(string outputArgs, int framerate = 30, int parallelJobs = 1)
        {
            if (string.IsNullOrWhiteSpace(outputArgs)
                || !outputArgs.Contains("libvpx-vp9", StringComparison.OrdinalIgnoreCase)
                || outputArgs.Contains("-deadline best", StringComparison.OrdinalIgnoreCase)
                || outputArgs.Contains("-deadline good", StringComparison.OrdinalIgnoreCase)
                || outputArgs.Contains("-quality best", StringComparison.OrdinalIgnoreCase)
                || outputArgs.Contains("-quality good", StringComparison.OrdinalIgnoreCase))
            {
                return outputArgs;
            }

            var optimized = Vp9SlowCpuUsedRegex.Replace(outputArgs, "-cpu-used 8");
            var threadsPerJob = parallelJobs > 1
                ? Math.Clamp(Environment.ProcessorCount / parallelJobs, 2, 16)
                : Math.Clamp(Environment.ProcessorCount, 4, 16);

            if (FfmpegThreadsRegex.IsMatch(optimized))
            {
                optimized = FfmpegThreadsRegex.Replace(optimized, $"-threads {threadsPerJob}");
            }

            var extraFlags = new StringBuilder();
            if (!optimized.Contains("-deadline", StringComparison.OrdinalIgnoreCase)
                && !optimized.Contains("-quality", StringComparison.OrdinalIgnoreCase))
            {
                extraFlags.Append(" -deadline realtime");
            }
            if (!optimized.Contains("-cpu-used", StringComparison.OrdinalIgnoreCase)
                && !optimized.Contains("-speed", StringComparison.OrdinalIgnoreCase))
            {
                extraFlags.Append(" -cpu-used 8");
            }
            if (!optimized.Contains("-threads", StringComparison.OrdinalIgnoreCase))
            {
                extraFlags.Append($" -threads {threadsPerJob}");
            }
            if (!optimized.Contains("-row-mt", StringComparison.OrdinalIgnoreCase))
            {
                extraFlags.Append(" -row-mt 1");
            }
            if (!optimized.Contains("-lag-in-frames", StringComparison.OrdinalIgnoreCase))
            {
                extraFlags.Append(" -lag-in-frames 0");
            }
            if (!optimized.Contains("-tile-columns", StringComparison.OrdinalIgnoreCase))
            {
                extraFlags.Append(" -tile-columns 2 -tile-rows 1");
            }
            if (!optimized.Contains("-frame-parallel", StringComparison.OrdinalIgnoreCase))
            {
                extraFlags.Append(" -frame-parallel 1");
            }
            if (!optimized.Contains("-static-thresh", StringComparison.OrdinalIgnoreCase))
            {
                extraFlags.Append(" -static-thresh 1000");
            }
            if (!optimized.Contains("-aq-mode", StringComparison.OrdinalIgnoreCase))
            {
                extraFlags.Append(" -aq-mode 3");
            }
            if (!optimized.Contains("-g ", StringComparison.OrdinalIgnoreCase))
            {
                // Maintain an 8-second keyframe interval across 30fps (-g 240) and 60fps (-g 480) for responsive NLE scrubbing
                int gopSize = Math.Clamp(Math.Max(1, framerate) * 8, 120, 600);
                extraFlags.Append($" -g {gopSize}");
            }
            if (!optimized.Contains("-undershoot-pct", StringComparison.OrdinalIgnoreCase))
            {
                extraFlags.Append(" -undershoot-pct 100");
            }

            if (extraFlags.Length > 0)
            {
                var savePathIdx = optimized.IndexOf("\"{save_path}\"", StringComparison.Ordinal);
                if (savePathIdx < 0)
                {
                    savePathIdx = optimized.IndexOf("{save_path}", StringComparison.Ordinal);
                }

                if (savePathIdx > 0)
                {
                    optimized = optimized.Insert(savePathIdx, extraFlags.ToString().TrimStart() + " ");
                }
            }

            return optimized;
        }

        private (SKBitmap Frame, long Version) GetFrameFromTickWithVersion(RenderWorkerState workerState, int currentTick, int sectionDefaultYPos, UpdateFrame currentFrame = null)
        {
            currentFrame ??= GenerateUpdateFrame(workerState, currentTick, sectionDefaultYPos);
            return DrawAnimatedEmotes(workerState, currentFrame, currentTick);
        }

        private (SKBitmap Frame, long Version) DrawAnimatedEmotes(RenderWorkerState workerState, UpdateFrame currentFrame, int currentTick)
        {
            var updateFrame = currentFrame.Image;
            if (!currentFrame.HasAnimatedEmotes)
            {
                workerState.InvalidateAnimCache();
                return (updateFrame, currentFrame.Version);
            }

            var comments = currentFrame.Comments;
            var commentIndex = currentFrame.CommentIndex;
            var currentTickMs = (long)(currentTick / (double)renderOptions.Framerate * 1000);

            // Fast O(1) short-circuit for high-framerate (60fps+) ticks: when the base UpdateFrame hasn't changed and
            // currentTickMs has not yet reached the earliest next frame transition of any visible animated emote,
            // return the cached composited frame immediately without iterating comments or running binary searches.
            if (workerState.AnimComposedFrame != null
                && commentIndex == workerState.AnimComposedForCommentIndex
                && currentFrame.Version == workerState.AnimBaseUpdateVersion
                && currentTickMs >= workerState.AnimLastTickMs
                && currentTickMs < workerState.AnimNextChangeTickMs)
            {
                workerState.AnimLastTickMs = currentTickMs;
                return (workerState.AnimComposedFrame, workerState.AnimComposedVersion);
            }

            if (workerState.AnimComposedFrame != null
                && commentIndex == workerState.AnimComposedForCommentIndex
                && currentFrame.Version == workerState.AnimBaseUpdateVersion
                && AnimCacheIsValid(workerState, comments, currentTickMs, out long nextValidTickMs))
            {
                workerState.AnimNextChangeTickMs = nextValidTickMs;
                workerState.AnimLastTickMs = currentTickMs;
                return (workerState.AnimComposedFrame, workerState.AnimComposedVersion);
            }

            ComposeAnimatedFrame(workerState, updateFrame, currentFrame.Version, comments, currentTickMs, out long nextChangeTickMs);
            workerState.AnimComposedForCommentIndex = commentIndex;
            workerState.AnimNextChangeTickMs = nextChangeTickMs;
            workerState.AnimLastTickMs = currentTickMs;
            workerState.AnimComposedVersion = ++workerState.FrameVersionCounter;
            return (workerState.AnimComposedFrame, workerState.AnimComposedVersion);
        }

        /// <summary>
        /// Composites the current animated-emote frames onto <paramref name="updateFrame"/> held in
        /// the persistent <see cref="RenderWorkerState.AnimComposedFrame"/> buffer. When <paramref name="updateVersion"/> is unchanged,
        /// only the 2x2-aligned vertical row band covering the specific animated emote(s) whose frame index advanced
        /// on this tick is restored and re-composited.
        /// </summary>
        private void ComposeAnimatedFrame(RenderWorkerState workerState, SKBitmap updateFrame, long updateVersion, List<CommentSection> comments, long currentTickMs, out long nextChangeTickMs)
        {
            if (workerState.AnimComposedFrame == null)
            {
                workerState.AnimComposedFrame = new SKBitmap(updateFrame.Info);
                workerState.AnimByteSize = updateFrame.Info.BytesSize;
                workerState.AnimCanvas = new SKCanvas(workerState.AnimComposedFrame);
                workerState.AnimBaseUpdateVersion = -1;
            }

            bool isIncremental = workerState.AnimBaseUpdateVersion == updateVersion;
            int minY = renderOptions.ChatHeight;
            int maxY = 0;
            nextChangeTickMs = long.MaxValue;

            var frameHeight = renderOptions.ChatHeight;
            var verticalPadding = renderOptions.VerticalPadding;
            int emoteIdx = 0;
            var animLastFrameIndices = workerState.AnimLastFrameIndices;

            for (var c = comments.Count - 1; c >= 0; c--)
            {
                var comment = comments[c];
                frameHeight -= comment.Image.Info.Height + verticalPadding;
                foreach (var (drawPoint, emote) in comment.Emotes)
                {
                    if (emote.FrameCount > 1)
                    {
                        int frameIdx = emote.GetFrameIndexAndNextChangeMs(currentTickMs, out long emoteNextChangeMs);
                        if (emoteNextChangeMs < nextChangeTickMs)
                        {
                            nextChangeTickMs = emoteNextChangeMs;
                        }

                        bool emoteChanged = !isIncremental
                            || emoteIdx >= animLastFrameIndices.Count
                            || animLastFrameIndices[emoteIdx] != frameIdx;

                        if (emoteIdx < animLastFrameIndices.Count)
                        {
                            animLastFrameIndices[emoteIdx] = frameIdx;
                        }
                        else
                        {
                            animLastFrameIndices.Add(frameIdx);
                        }

                        if (emoteChanged)
                        {
                            var emoteImg = emote.EmoteFrames[frameIdx];
                            int top = Math.Max(0, drawPoint.Y + frameHeight);
                            int bottom = Math.Min(renderOptions.ChatHeight, drawPoint.Y + frameHeight + emoteImg.Height);
                            if (top < minY) minY = top;
                            if (bottom > maxY) maxY = bottom;
                        }

                        emoteIdx++;
                    }
                }
            }

            if (animLastFrameIndices.Count > emoteIdx)
            {
                animLastFrameIndices.RemoveRange(emoteIdx, animLastFrameIndices.Count - emoteIdx);
            }

            minY = Math.Max(0, minY & ~1);
            maxY = Math.Min(renderOptions.ChatHeight, (maxY + 1) & ~1);

            unsafe
            {
                byte* pSrc = (byte*)updateFrame.GetPixels();
                byte* pDst = (byte*)workerState.AnimComposedFrame.GetPixels();

                if (!isIncremental)
                {
                    Buffer.MemoryCopy(pSrc, pDst, workerState.AnimByteSize, workerState.AnimByteSize);
                    workerState.AnimBaseUpdateVersion = updateVersion;
                }
                else if (maxY > minY)
                {
                    long rowOffsetBytes = (long)minY * renderOptions.ChatWidth * 4;
                    long copyLengthBytes = (long)(maxY - minY) * renderOptions.ChatWidth * 4;
                    Buffer.MemoryCopy(pSrc + rowOffsetBytes, pDst + rowOffsetBytes, copyLengthBytes, copyLengthBytes);
                }
            }

            workerState.AnimDirtyMinY = minY;
            workerState.AnimDirtyMaxY = maxY;

            if (maxY <= minY)
            {
                return;
            }

            if (isIncremental)
            {
                workerState.AnimCanvas.Save();
                workerState.AnimCanvas.ClipRect(new SKRect(0, minY, renderOptions.ChatWidth, maxY));
            }

            try
            {
                frameHeight = renderOptions.ChatHeight;
                int drawEmoteIdx = 0;
                for (var c = comments.Count - 1; c >= 0; c--)
                {
                    var comment = comments[c];
                    frameHeight -= comment.Image.Info.Height + verticalPadding;
                    foreach (var (drawPoint, emote) in comment.Emotes)
                    {
                        if (emote.FrameCount > 1)
                        {
                            var emoteImg = emote.EmoteFrames[animLastFrameIndices[drawEmoteIdx++]];
                            int drawY = drawPoint.Y + frameHeight;
                            if (!isIncremental || (drawY < maxY && drawY + emoteImg.Height > minY))
                            {
                                workerState.AnimCanvas.DrawImage(emoteImg, drawPoint.X, drawY);
                            }
                        }
                    }
                }
            }
            finally
            {
                if (isIncremental)
                {
                    workerState.AnimCanvas.Restore();
                }
            }
        }

        /// <summary>Returns <see langword="true"/> when every animated emote is on the same frame index as when the cache was built.</summary>
        private static bool AnimCacheIsValid(RenderWorkerState workerState, List<CommentSection> comments, long currentTickMs, out long nextChangeTickMs)
        {
            nextChangeTickMs = long.MaxValue;
            var animLastFrameIndices = workerState.AnimLastFrameIndices;
            var i = 0;
            for (var c = comments.Count - 1; c >= 0; c--)
            {
                foreach (var (_, emote) in comments[c].Emotes)
                {
                    if (emote.FrameCount > 1)
                    {
                        if (i >= animLastFrameIndices.Count) return false;
                        if (emote.GetFrameIndexAndNextChangeMs(currentTickMs, out long emoteNextMs) != animLastFrameIndices[i]) return false;
                        if (emoteNextMs < nextChangeTickMs) nextChangeTickMs = emoteNextMs;
                        i++;
                    }
                }
            }

            return i == animLastFrameIndices.Count;
        }

        private int GetNewestCommentIndex(int lastIndex, double currentTimeSeconds)
        {
            var commentSpan = CollectionsMarshal.AsSpan(chatRoot.comments);
            for (var i = lastIndex + 1; i < commentSpan.Length; i++)
            {
                if (commentSpan[i].content_offset_seconds > currentTimeSeconds)
                {
                    return i - 1;
                }
            }

            return commentSpan.Length - 1;
        }

        /// <summary>
        /// Pre-renders upcoming <see cref="CommentSection"/>s in parallel batches across CPU cores and pre-materializes
        /// their lazy emote/GIF frames so the frame loop never stalls on single-threaded text path stroking or GIF decoding.
        /// </summary>
        private void EnsureLookaheadPreRendered(RenderWorkerState workerState, int startIndex, int requiredUpToIndex, int sectionDefaultYPos)
        {
            var maxCommentIdx = workerState.MaxCommentIndex >= 0
                ? Math.Min(workerState.MaxCommentIndex, chatRoot.comments.Count - 1)
                : chatRoot.comments.Count - 1;

            if (requiredUpToIndex <= workerState.PreRenderedUpToIndex || startIndex > maxCommentIdx)
            {
                return;
            }

            var batchStart = Math.Max(startIndex, workerState.PreRenderedUpToIndex + 1);
            var lookaheadCount = Math.Clamp(Environment.ProcessorCount * 8 / Math.Max(1, workerState.TotalWorkers), 16, 128);
            var batchEnd = Math.Min(maxCommentIdx, Math.Max(requiredUpToIndex, batchStart + lookaheadCount - 1));
            var count = batchEnd - batchStart + 1;
            if (count <= 0)
            {
                return;
            }

            if (count == 1)
            {
                var section = GenerateCommentSection(batchStart, sectionDefaultYPos, _workerContexts.Value!);
                if (section != null)
                {
                    _imageBudget.MarkDrawn(section.Emotes, workerState.WorkerId);
                    foreach (var (_, emote) in section.Emotes)
                    {
                        _ = emote.EmoteFrames;
                    }
                }
                workerState.PreRenderedSections[batchStart] = section;
                workerState.PreRenderedUpToIndex = batchEnd;
                return;
            }

            var results = new CommentSection[count];
            var maxParallel = Math.Clamp(Environment.ProcessorCount / Math.Max(1, workerState.TotalWorkers), 2, 16);
            Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = maxParallel }, offset =>
            {
                var commentIdx = batchStart + offset;
                var section = GenerateCommentSection(commentIdx, sectionDefaultYPos, _workerContexts.Value!);
                if (section != null)
                {
                    _imageBudget.MarkDrawn(section.Emotes, workerState.WorkerId);
                    foreach (var (_, emote) in section.Emotes)
                    {
                        _ = emote.EmoteFrames;
                    }
                }
                results[offset] = section;
            });

            for (int offset = 0; offset < count; offset++)
            {
                workerState.PreRenderedSections[batchStart + offset] = results[offset];
            }

            workerState.PreRenderedUpToIndex = batchEnd;
        }

        private UpdateFrame GenerateUpdateFrame(RenderWorkerState workerState, int currentTick, int sectionDefaultYPos, UpdateFrame lastUpdate = null)
        {
            var currentTimeSeconds = currentTick / (double)renderOptions.Framerate;
            var newestCommentIndex = GetNewestCommentIndex(lastUpdate?.CommentIndex ?? -1, currentTimeSeconds);
            if (lastUpdate is not null && newestCommentIndex == lastUpdate.CommentIndex)
            {
                return lastUpdate;
            }

            var isNewUpdateFrame = false;
            if (lastUpdate is null)
            {
                var bitmap = new SKBitmap(renderOptions.ChatWidth, renderOptions.ChatHeight);
                lastUpdate = new UpdateFrame
                {
                    Image = bitmap,
                    Canvas = new SKCanvas(bitmap)
                };
                isNewUpdateFrame = true;
            }

            var commentList = lastUpdate.Comments ?? [];

            int oldCommentIndex = -1;
            if (commentList.Count > 0)
            {
                oldCommentIndex = commentList[^1].CommentIndex;
            }
            else if (newestCommentIndex > 100)
            {
                // If we are starting partially through the comment list, we don't want to needlessly render *every* comment before our starting comment.
                // Skipping to 100 comments before our starting index should be more than enough to fill the frame with previous comments
                oldCommentIndex = newestCommentIndex - 100;
            }

            var anyCommentAdded = false;
            if (newestCommentIndex > oldCommentIndex)
            {
                EnsureLookaheadPreRendered(workerState, oldCommentIndex + 1, newestCommentIndex, sectionDefaultYPos);

                for (int currentIndex = oldCommentIndex + 1; currentIndex <= newestCommentIndex; currentIndex++)
                {
                    if (!workerState.PreRenderedSections.Remove(currentIndex, out var comment))
                    {
                        comment = GenerateCommentSection(currentIndex, sectionDefaultYPos, _workerContexts.Value!);
                    }

                    if (comment != null)
                    {
                        _imageBudget.MarkDrawn(comment.Emotes, workerState.WorkerId);
                        commentList.Add(comment);
                        anyCommentAdded = true;
                    }
                }
            }

            // If all new comments in this interval were filtered out (e.g. skipped sub notifications) and the frame
            // was already initialized, no visual change occurred on screen.
            if (!anyCommentAdded && !isNewUpdateFrame)
            {
                lastUpdate.CommentIndex = newestCommentIndex;
                return lastUpdate;
            }

            var frameCanvas = lastUpdate.Canvas;
            int commentsDrawn = 0;
            int commentListIndex = commentList.Count - 1;
            int frameHeight = renderOptions.ChatHeight;
            var frameWidth = lastUpdate.Image.Width;
            var hasAnimatedEmotes = false;
            frameCanvas.Clear(renderOptions.BackgroundColor);

            while (commentListIndex >= 0 && frameHeight > -renderOptions.VerticalPadding)
            {
                var comment = commentList[commentListIndex];
                var commentHeight = comment.Image.Info.Height;
                frameHeight -= commentHeight + renderOptions.VerticalPadding;

                var backgroundColor = GetMessageBackground(comment.CommentIndex, _workerContexts.Value!.PaintCache, out var backgroundPaint);
                if (backgroundColor != renderOptions.BackgroundColor)
                {
                    frameCanvas.DrawRect(0, frameHeight - renderOptions.VerticalPadding / 2f, frameWidth, commentHeight + renderOptions.VerticalPadding, backgroundPaint);
                }

                frameCanvas.DrawImage(comment.Image, 0, frameHeight);

                foreach (var (drawPoint, emote) in comment.Emotes)
                {
                    if (emote.FrameCount == 1)
                    {
                        frameCanvas.DrawImage(emote.EmoteFrames[0], drawPoint.X, drawPoint.Y + frameHeight);
                    }
                    else
                    {
                        hasAnimatedEmotes = true;
                    }
                }
                commentsDrawn++;
                commentListIndex--;
            }

            int removeCount = commentList.Count - commentsDrawn;
            for (int i = 0; i < removeCount; i++)
            {
                commentList[i].Image.Dispose();
            }
            commentList.RemoveRange(0, removeCount);

            if (removeCount > 0)
            {
                PopulateOnScreenImages(commentList, workerState.PreRenderedSections, workerState.OnScreenEmoteSet);
                if (_imageBudget.ReleaseUnusedTracked(workerState.OnScreenEmoteSet, workerState.WorkerId))
                {
                    workerState.InvalidateAnimCache();
                }
            }

            lastUpdate.Comments = commentList;
            lastUpdate.CommentIndex = newestCommentIndex;
            lastUpdate.HasAnimatedEmotes = hasAnimatedEmotes;
            lastUpdate.Version = ++workerState.FrameVersionCounter;
            return lastUpdate;
        }

        private SKColor GetMessageBackground(int commentIndex, DisposableDictionary<SKColor, SKPaint> paintCache, [AllowNull] out SKPaint paint)
        {
            var commenter = chatRoot.comments[commentIndex].commenter;
            var isHighlighted = renderOptions.HighlightUsersArray.Length > 0
                                && (renderOptions.HighlightUsersArray.Contains(commenter.name, StringComparer.OrdinalIgnoreCase)
                                    || renderOptions.HighlightUsersArray.Contains(commenter.display_name, StringComparer.OrdinalIgnoreCase));

            if (isHighlighted)
            {
                if (renderOptions.AlternateMessageBackgrounds && commentIndex % 2 == 1)
                {
                    paint = renderOptions.AlternateBackgroundHighlightUserPaint;
                    return paint.Color;
                }

                paint = GetCachedPaint(renderOptions.HighlightUserColor, paintCache);
                return renderOptions.HighlightUserColor;
            }

            if (renderOptions.AlternateMessageBackgrounds && commentIndex % 2 == 1)
            {
                paint = renderOptions.AlternateBackgroundPaint;
                return renderOptions.AlternateBackgroundColor;
            }

            paint = null;
            return renderOptions.BackgroundColor;
        }

        private CommentSection GenerateCommentSection(int commentIndex, int sectionDefaultYPos, SectionRenderContext ctx)
        {
            List<EmotePosition> emoteSectionList = new List<EmotePosition>();
            Comment comment = chatRoot.comments[commentIndex];
            List<SectionImage> sectionImages = [];
            Point drawPos = new Point();
            Point defaultPos = new Point();
            var highlightType = HighlightType.Unknown;
            defaultPos.X = renderOptions.SidePadding;

            if (comment.message.user_notice_params?.msg_id != null)
            {
                if (comment.message.user_notice_params.msg_id is not "highlighted-message" and not "sub" and not "resub" and not "subgift" and not "")
                {
                    _progress.LogVerbose($"{comment._id} has invalid {nameof(comment.message.user_notice_params)}: {comment.message.user_notice_params.msg_id}.");
                    return null;
                }

                if (comment.message.user_notice_params.msg_id == "highlighted-message")
                {
                    if (comment.message.fragments == null && comment.message.body != null)
                    {
                        comment = comment.Clone();
                        comment.message.fragments = [new Fragment { text = comment.message.body }];
                    }

                    highlightType = HighlightType.ChannelPointHighlight;
                }
            }

            if (comment.message.fragments == null || comment.commenter == null)
            {
                _progress.LogVerbose($"{comment._id} lacks fragments and/or a commenter.");
                return null;
            }

            AddImageSection(sectionImages, ref drawPos, defaultPos);
            defaultPos.Y = sectionDefaultYPos;
            drawPos.Y = defaultPos.Y;

            if (highlightType is HighlightType.Unknown)
            {
                highlightType = HighlightIcons.GetHighlightType(comment);
            }

            if (highlightType is not HighlightType.None)
            {
                if (highlightType is not HighlightType.ChannelPointHighlight && !renderOptions.SubMessages)
                {
                    foreach (var img in sectionImages)
                    {
                        _sectionImageCache.Return(img);
                    }
                    sectionImages.Clear();
                    return null;
                }

                // Clone accented messages before rendering so in-place substring adjustments in sub/resub/watch-streak/bits-badge
                // handlers never mutate shared chatRoot.comments across parallel segment workers.
                comment = comment.Clone();
                DrawAccentedMessage(comment, sectionImages, emoteSectionList, highlightType, commentIndex, ref drawPos, defaultPos, ctx);
            }
            else
            {
                DrawNonAccentedMessage(comment, sectionImages, emoteSectionList, false, commentIndex, ref drawPos, ref defaultPos, ctx);
            }

            return new CommentSection
            {
                Image = CombineImages(sectionImages, highlightType, commentIndex, ctx),
                Emotes = emoteSectionList,
                CommentIndex = commentIndex,
            };
        }

        private SKImage CombineImages(List<SectionImage> sectionImages, HighlightType highlightType, int commentIndex, SectionRenderContext ctx)
        {
            var totalHeight = 0;
            for (int i = 0; i < sectionImages.Count; i++)
            {
                totalHeight += sectionImages[i].Info.Height;
            }

            var rentedImage = _sectionImageCache.Rent(renderOptions.ChatWidth, totalHeight);
            var bitmapInfo = rentedImage.Info;
            var canvas = rentedImage.Canvas;

            if (highlightType is HighlightType.PayingForward or HighlightType.ChannelPointHighlight or HighlightType.WatchStreak or HighlightType.Combo)
            {
                var accentColor = highlightType is HighlightType.PayingForward
                    ? new SKColor(0xFF26262C) // AARRGGBB
                    : new SKColor(0xFF80808C); // AARRGGBB

                var paint = GetCachedPaint(accentColor, ctx.PaintCache);
                canvas.DrawRect(renderOptions.SidePadding, 0, renderOptions.AccentStrokeWidth, bitmapInfo.Height, paint);
            }
            else if (highlightType is not HighlightType.None)
            {
                const int OPAQUE_THRESHOLD = 245;
                var messageBackground = GetMessageBackground(commentIndex, ctx.PaintCache, out _);
                if (messageBackground.Alpha < OPAQUE_THRESHOLD)
                {
                    // Draw the highlight background only if the message background is opaque enough
                    var backgroundColor = new SKColor(0x1A6B6B6E); // AARRGGBB
                    var backgroundPaint = GetCachedPaint(backgroundColor, ctx.PaintCache);
                    canvas.DrawRect(renderOptions.SidePadding, 0, bitmapInfo.Width - renderOptions.SidePadding * 2, bitmapInfo.Height, backgroundPaint);
                }

                var accentPaint = GetCachedPaint(Purple, ctx.PaintCache);
                canvas.DrawRect(renderOptions.SidePadding, 0, renderOptions.AccentStrokeWidth, bitmapInfo.Height, accentPaint);
            }

            for (var i = 0; i < sectionImages.Count; i++)
            {
                canvas.DrawBitmap(sectionImages[i].Bitmap, 0, i * renderOptions.SectionHeight);
                _sectionImageCache.Return(sectionImages[i]);
            }
            sectionImages.Clear();

            rentedImage.Flush();
            var finalImage = SKImage.FromBitmap(rentedImage.Bitmap);
            _sectionImageCache.Return(rentedImage);
            return finalImage;
        }

        private void DrawNonAccentedMessage(Comment comment, List<SectionImage> sectionImages, List<EmotePosition> emotePositionList, bool highlightWords, int commentIndex, ref Point drawPos, ref Point defaultPos, SectionRenderContext ctx)
        {
            if (renderOptions.Timestamp)
            {
                DrawTimestamp(comment, sectionImages, ref drawPos, ref defaultPos, ctx);
            }
            if (renderOptions.RenderUserAvatars)
            {
                DrawAvatar(comment, sectionImages, ref drawPos);
            }
            if (renderOptions.ChatBadges)
            {
                DrawBadges(comment, sectionImages, ref drawPos);
            }
            DrawUsername(comment, sectionImages, ref drawPos, defaultPos, ctx, commentIndex: commentIndex);
            DrawMessage(comment, sectionImages, emotePositionList, highlightWords, ref drawPos, defaultPos, ctx);

            foreach (var sectionImage in sectionImages)
            {
                sectionImage.Flush();
            }
        }

        private void DrawAccentedMessage(Comment comment, List<SectionImage> sectionImages, List<EmotePosition> emotePositionList, HighlightType highlightType, int commentIndex, ref Point drawPos, Point defaultPos, SectionRenderContext ctx)
        {
            drawPos.X += renderOptions.AccentIndentWidth;
            defaultPos.X = drawPos.X;

            var highlightIcon = highlightIcons.GetHighlightIcon(highlightType, ctx.MessageFont.Color);

            Point iconPoint = new()
            {
                X = drawPos.X,
                Y = (int)((renderOptions.SectionHeight - highlightIcon.Height) / 2.0)
            };

            switch (highlightType)
            {
                case HighlightType.SubscribedTier:
                case HighlightType.SubscribedPrime:
                    DrawSubscribeMessage(comment, sectionImages, emotePositionList, commentIndex, ref drawPos, defaultPos, highlightIcon, iconPoint, ctx);
                    break;
                case HighlightType.BitBadgeTierNotification:
                    DrawBitsBadgeTierMessage(comment, sectionImages, emotePositionList, ref drawPos, defaultPos, highlightIcon, iconPoint, ctx);
                    break;
                case HighlightType.WatchStreak:
                    DrawWatchStreakMessage(comment, sectionImages, emotePositionList, commentIndex, ref drawPos, defaultPos, highlightIcon, iconPoint, ctx);
                    break;
                case HighlightType.CharityDonation:
                    DrawCharityDonationMessage(comment, sectionImages, emotePositionList, ref drawPos, defaultPos, highlightIcon, iconPoint, ctx);
                    break;
                case HighlightType.GiftedMany:
                case HighlightType.GiftedSingle:
                case HighlightType.GiftedAnonymous:
                case HighlightType.ContinuingAnonymousGift:
                    DrawGiftMessage(comment, sectionImages, emotePositionList, ref drawPos, defaultPos, highlightIcon, iconPoint, ctx);
                    break;
                case HighlightType.ChannelPointHighlight:
                    DrawNonAccentedMessage(comment, sectionImages, emotePositionList, true, commentIndex, ref drawPos, ref defaultPos, ctx);
                    break;
                case HighlightType.ContinuingGift:
                case HighlightType.PayingForward:
                case HighlightType.Raid:
                case HighlightType.Combo:
                default:
                    DrawMessage(comment, sectionImages, emotePositionList, false, ref drawPos, defaultPos, ctx);
                    break;
            }

            foreach (var sectionImage in sectionImages)
            {
                sectionImage.Flush();
            }
        }

        private void DrawSubscribeMessage(Comment comment, List<SectionImage> sectionImages, List<EmotePosition> emotePositionList, int commentIndex, ref Point drawPos, Point defaultPos, SKImage highlightIcon, Point iconPoint, SectionRenderContext ctx)
        {
            var canvas = sectionImages[^1].Canvas;
            canvas.DrawImage(highlightIcon, iconPoint.X, iconPoint.Y);

            Point customMessagePos = drawPos;
            drawPos.X += highlightIcon.Width + renderOptions.WordSpacing;
            defaultPos.X = drawPos.X;

            DrawUsername(comment, sectionImages, ref drawPos, defaultPos, ctx, false, Purple);
            AddImageSection(sectionImages, ref drawPos, defaultPos);

            // Remove the commenter's name from the resub message
            comment.message.body = comment.message.body[(comment.commenter.display_name.Length + 1)..];
            if (comment.message.fragments[0].text.Equals(comment.commenter.display_name, StringComparison.OrdinalIgnoreCase))
            {
                // Some older chat replays separate user names into separate fragments
                comment.message.fragments.RemoveAt(0);
            }
            else
            {
                comment.message.fragments[0].text = comment.message.fragments[0].text[(comment.commenter.display_name.Length + 1)..];
            }

            var (resubMessage, customResubMessage) = HighlightIcons.SplitSubComment(comment);
            DrawMessage(resubMessage, sectionImages, emotePositionList, false, ref drawPos, defaultPos, ctx);

            // Return if there is no custom resub message to draw
            if (customResubMessage is null)
            {
                return;
            }

            AddImageSection(sectionImages, ref drawPos, defaultPos);
            drawPos = customMessagePos;
            defaultPos = customMessagePos;
            DrawNonAccentedMessage(customResubMessage, sectionImages, emotePositionList, false, commentIndex, ref drawPos, ref defaultPos, ctx);
        }

        private void DrawBitsBadgeTierMessage(Comment comment, List<SectionImage> sectionImages, List<EmotePosition> emotePositionList, ref Point drawPos, Point defaultPos, SKImage highlightIcon, Point iconPoint, SectionRenderContext ctx)
        {
            var canvas = sectionImages[^1].Canvas;

            canvas.DrawImage(highlightIcon, iconPoint.X, iconPoint.Y);
            drawPos.X += highlightIcon.Width + renderOptions.WordSpacing;
            defaultPos.X = drawPos.X;

            if (comment.message.fragments.Count == 1)
            {
                DrawUsername(comment, sectionImages, ref drawPos, defaultPos, ctx, false, ctx.MessageFont.Color);

                var bitsBadgeVersion = comment.message.user_badges.FirstOrDefault(x => x._id == "bits")?.version;
                if (bitsBadgeVersion is not null)
                {
                    comment.message.body = bitsBadgeVersion.Length > 3
                        ? $"just earned a new {bitsBadgeVersion.AsSpan(0, bitsBadgeVersion.Length - 3)}K Bits badge!"
                        : $"just earned a new {bitsBadgeVersion} Bits badge!";
                }
                else
                {
                    comment.message.body = "just earned a new Bits badge!";
                }

                comment.message.fragments[0].text = comment.message.body;
            }
            else
            {
                // This should never be possible, but just in case.
                DrawUsername(comment, sectionImages, ref drawPos, defaultPos, ctx, true, ctx.MessageFont.Color);
            }

            DrawMessage(comment, sectionImages, emotePositionList, false, ref drawPos, defaultPos, ctx);
        }

        private void DrawWatchStreakMessage(Comment comment, List<SectionImage> sectionImages, List<EmotePosition> emotePositionList, int commentIndex, ref Point drawPos, Point defaultPos, SKImage highlightIcon, Point iconPoint, SectionRenderContext ctx)
        {
            var canvas = sectionImages[^1].Canvas;
            canvas.DrawImage(highlightIcon, iconPoint.X, iconPoint.Y);

            Point customMessagePos = drawPos;
            drawPos.X += highlightIcon.Width + renderOptions.WordSpacing;
            defaultPos.X = drawPos.X;

            DrawUsername(comment, sectionImages, ref drawPos, defaultPos, ctx, false, Purple);
            AddImageSection(sectionImages, ref drawPos, defaultPos);

            // Remove the commenter's name from the watch streak message
            comment.message.body = comment.message.body[(comment.commenter.display_name.Length + 1)..];
            if (comment.message.fragments[0].text.Equals(comment.commenter.display_name, StringComparison.OrdinalIgnoreCase))
            {
                // This is necessary for sub messages. We'll keep it around just in case.
                comment.message.fragments.RemoveAt(0);
            }
            else
            {
                comment.message.fragments[0].text = comment.message.fragments[0].text[(comment.commenter.display_name.Length + 1)..];
            }

            var (streakMessage, customMessage) = HighlightIcons.SplitWatchStreakComment(comment);
            DrawMessage(streakMessage, sectionImages, emotePositionList, false, ref drawPos, defaultPos, ctx);

            // Return if there is no custom message to draw
            if (customMessage is null)
            {
                return;
            }

            AddImageSection(sectionImages, ref drawPos, defaultPos);
            drawPos = customMessagePos;
            defaultPos = customMessagePos;
            DrawNonAccentedMessage(customMessage, sectionImages, emotePositionList, false, commentIndex, ref drawPos, ref defaultPos, ctx);
        }

        private void DrawCharityDonationMessage(Comment comment, List<SectionImage> sectionImages, List<EmotePosition> emotePositionList, ref Point drawPos, Point defaultPos, SKImage highlightIcon, Point iconPoint, SectionRenderContext ctx)
        {
            var canvas = sectionImages[^1].Canvas;
            canvas.DrawImage(highlightIcon, iconPoint.X, iconPoint.Y);

            drawPos.X += highlightIcon.Width + renderOptions.WordSpacing;
            defaultPos.X = drawPos.X;

            DrawUsername(comment, sectionImages, ref drawPos, defaultPos, ctx, false, Purple);
            AddImageSection(sectionImages, ref drawPos, defaultPos);

            // Remove the commenter's name from the charity donation message
            comment.message.body = comment.message.body[(comment.commenter.display_name.Length + 2)..];
            comment.message.fragments[0].text = comment.message.fragments[0].text[(comment.commenter.display_name.Length + 2)..];

            DrawMessage(comment, sectionImages, emotePositionList, false, ref drawPos, defaultPos, ctx);
        }

        private void DrawGiftMessage(Comment comment, List<SectionImage> sectionImages, List<EmotePosition> emotePositionList, ref Point drawPos, Point defaultPos, SKImage highlightIcon, Point iconPoint, SectionRenderContext ctx)
        {
            var canvas = sectionImages[^1].Canvas;

            canvas.DrawImage(highlightIcon, iconPoint.X, iconPoint.Y);
            drawPos.X += highlightIcon.Width + renderOptions.AccentIndentWidth - renderOptions.AccentStrokeWidth;
            defaultPos.X = drawPos.X;
            DrawMessage(comment, sectionImages, emotePositionList, false, ref drawPos, defaultPos, ctx);
        }

        private static readonly SearchValues<char> WhiteSpaceChars = SearchValues.Create("\t\n\v\f\r\u0020\u0085\u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000");

        private void DrawMessage(Comment comment, List<SectionImage> sectionImages, List<EmotePosition> emotePositionList, bool highlightWords, ref Point drawPos, Point defaultPos, SectionRenderContext ctx)
        {
            // Detection is deliberately not gated on the GiphyGifs option: a posted GIF is its own kind of message, and its
            // title must never be emote substituted. The option only decides whether the image itself is drawn.
            if (GiphyResolver.TryParseAltText(comment.message.body, out var gifTitle))
            {
                if (renderOptions.GiphyGifs && _gifCache.TryGetValue(gifTitle, out var gif))
                {
                    DrawChatGif(sectionImages, emotePositionList, ref drawPos, defaultPos, gif);
                }
                else
                {
                    DrawChatGifAltText(comment.message.body, sectionImages, emotePositionList, highlightWords, ref drawPos, defaultPos, ctx);
                }

                return;
            }

            int bitsCount = comment.message.bits_spent;
            foreach (var fragment in comment.message.fragments)
            {
                if (fragment.emoticon != null)
                {
                    DrawFirstPartyEmote(sectionImages, emotePositionList, ref drawPos, defaultPos, fragment, highlightWords, ctx);
                    continue;
                }

                // Either text or third party emote
                var fragmentSpan = fragment.text.AsSpan();
                var spaceCount = fragmentSpan.CountAny(WhiteSpaceChars);

                var fragmentParts = ArrayPool<Range>.Shared.Rent(spaceCount + 1);
                try
                {
                    var written = SwapRightToLeft(fragmentSpan.SplitAny(WhiteSpaceChars), fragmentParts);
                    foreach (var range in fragmentParts.Take(written))
                    {
                        var fragmentPart = fragmentSpan[range];
                        DrawFragmentPart(sectionImages, emotePositionList, ref drawPos, defaultPos, bitsCount, fragmentPart, highlightWords, ctx);
                    }
                }
                finally
                {
                    ArrayPool<Range>.Shared.Return(fragmentParts);
                }
            }
        }

        // A GIF that could not be resolved falls back to its title, drawn verbatim. The title comes from Giphy
        // rather than from chat, so a word in it that happens to match an emote is a coincidence, and substituting
        // one would render nonsense like "[Horse <emote> GIF by Jan Metternich]". Emoji are still drawn, since
        // those are characters in the title rather than a lookup.
        private void DrawChatGifAltText(string body, List<SectionImage> sectionImages, List<EmotePosition> emotePositionList, bool highlightWords, ref Point drawPos, Point defaultPos, SectionRenderContext ctx)
        {
            var bodySpan = body.AsSpan();
            var spaceCount = bodySpan.CountAny(WhiteSpaceChars);

            var bodyParts = ArrayPool<Range>.Shared.Rent(spaceCount + 1);
            try
            {
                var written = SwapRightToLeft(bodySpan.SplitAny(WhiteSpaceChars), bodyParts);
                foreach (var range in bodyParts.Take(written))
                {
                    DrawFragmentPart(sectionImages, emotePositionList, ref drawPos, defaultPos, 0, bodySpan[range], highlightWords, ctx, skipThird: true);
                }
            }
            finally
            {
                ArrayPool<Range>.Shared.Return(bodyParts);
            }
        }

        // Draws a GIF posted in chat on its own line(s) below the username, as wide as the height cap allows
        private void DrawChatGif(List<SectionImage> sectionImages, List<EmotePosition> emotePositionList, ref Point drawPos, Point defaultPos, TwitchEmote gif)
        {
            var gifInfo = gif.Info;

            // Sections are fixed height, so reserve as many blank ones as the image spans. Like emotes it is
            // composited in later rather than drawn here, so the animation can play.
            var sectionsNeeded = Math.Max(1, (int)Math.Ceiling(gifInfo.Height / (double)renderOptions.SectionHeight));
            for (var i = 0; i < sectionsNeeded; i++)
            {
                AddImageSection(sectionImages, ref drawPos, defaultPos);
            }

            var gifTop = (sectionImages.Count - sectionsNeeded) * renderOptions.SectionHeight;
            emotePositionList.Add(new EmotePosition(new Point { X = renderOptions.SidePadding, Y = gifTop }, gif));

            drawPos.X = renderOptions.SidePadding + gifInfo.Width;
        }

        private void DrawFragmentPart(List<SectionImage> sectionImages, List<EmotePosition> emotePositionList, ref Point drawPos, Point defaultPos, int bitsCount, ReadOnlySpan<char> fragmentPart, bool highlightWords, SectionRenderContext ctx, bool skipThird = false, bool skipEmoji = false, bool skipNonFont = false)
        {
            var thirdLookup = _emoteThirdCache.GetAlternateLookup<ReadOnlySpan<char>>();
            if (!skipThird && thirdLookup.TryGetValue(fragmentPart, out var emote))
            {
                DrawThirdPartyEmote(sectionImages, emotePositionList, ref drawPos, defaultPos, emote, highlightWords, ctx);
            }
            else if (bitsCount > 0 && TryDrawBits(sectionImages, emotePositionList, ref drawPos, defaultPos, bitsCount, fragmentPart))
            {
                // no-op
            }
            else if (!skipEmoji && renderOptions.EmojiVendor != EmojiVendor.None && ContainsEmoji(fragmentPart, out var firstEmoji))
            {
                DrawEmojiMessage(sectionImages, emotePositionList, ref drawPos, defaultPos, bitsCount, fragmentPart, highlightWords, ctx, firstEmoji);
            }
            else if (!skipNonFont && (!ctx.MessageFont.ContainsGlyphs(fragmentPart) || fragmentPart.LengthInTextElements() < fragmentPart.Length))
            {
                DrawNonFontMessage(sectionImages, ref drawPos, defaultPos, fragmentPart, highlightWords, ctx);
            }
            else
            {
                DrawText(fragmentPart, ctx.MessageFont, true, sectionImages, ref drawPos, defaultPos, highlightWords, ctx);
            }
        }

        private void DrawThirdPartyEmote(List<SectionImage> sectionImages, List<EmotePosition> emotePositionList, ref Point drawPos, Point defaultPos, TwitchEmote twitchEmote, bool highlightWords, SectionRenderContext ctx)
        {
            SKImageInfo emoteInfo = twitchEmote.Info;
            Point emotePoint = new Point();
            if (!twitchEmote.IsZeroWidth)
            {
                if (drawPos.X + emoteInfo.Width > renderOptions.ChatWidth - renderOptions.SidePadding * 2)
                {
                    AddImageSection(sectionImages, ref drawPos, defaultPos);
                }

                if (highlightWords)
                {
                    var canvas = sectionImages[^1].Canvas;
                    var paint = GetCachedPaint(Purple, ctx.PaintCache);
                    canvas.DrawRect(drawPos.X, 0, emoteInfo.Width + renderOptions.EmoteSpacing, renderOptions.SectionHeight, paint);
                }

                emotePoint.X = drawPos.X;
                drawPos.X += emoteInfo.Width + renderOptions.EmoteSpacing;
            }
            else
            {
                emotePoint.X = drawPos.X - renderOptions.EmoteSpacing - emoteInfo.Width;
            }
            emotePoint.Y = (int)((sectionImages.Count - 1) * renderOptions.SectionHeight + ((renderOptions.SectionHeight - emoteInfo.Height) / 2.0));
            emotePositionList.Add(new EmotePosition(emotePoint, twitchEmote));
        }

        private static readonly SearchValues<char> EmojiExcludeChars = SearchValues.Create('\uFE0F');

        private bool ContainsEmoji(ReadOnlySpan<char> text, out int firstEmoji)
        {
            if (!text.ContainsAnyExceptInRange('\0', '\u007F'))
            {
                firstEmoji = -1;
                return false;
            }

            var emojiPathLookup = _emojiPaths.GetAlternateLookup<ReadOnlySpan<char>>();
            Span<char> stackSpace = stackalloc char[16];

            var fragmentSlice = text;
            var index = 0;
            while (true)
            {
                var elementLength = StringInfo.GetNextTextElementLength(fragmentSlice);
                if (elementLength == 0)
                {
                    firstEmoji = -1;
                    return false;
                }

                if (elementLength == 1 && char.IsAscii(fragmentSlice[0]))
                {
                    fragmentSlice = fragmentSlice[elementLength..];
                    index += elementLength;
                    continue;
                }

                var textElement = fragmentSlice[..elementLength];
                foreach (var range in textElement.Split(ZERO_WIDTH_JOINER)) // Will return textElement once when no ZWJ
                {
                    var subEmoji = textElement[range];

                    var lookupKey = subEmoji.Length <= stackSpace.Length ? stackSpace : new char[subEmoji.Length];
                    var written = subEmoji.CopyToExcept(stackSpace, EmojiExcludeChars);
                    if (emojiPathLookup.ContainsKey(lookupKey[..written]))
                    {
                        firstEmoji = index;
                        return true;
                    }
                }

                fragmentSlice = fragmentSlice[elementLength..];
                index += elementLength;
            }
        }

        /// <summary>
        /// Retrieves a scaled <see cref="SKImage"/> for the given emoji sequence key, decoding and scaling the PNG on demand on first use.
        /// </summary>
        private bool TryGetEmojiImage(ReadOnlySpan<char> keySpan, [MaybeNullWhen(false)] out SKImage emojiImage)
        {
            var pathLookup = _emojiPaths.GetAlternateLookup<ReadOnlySpan<char>>();
            if (!pathLookup.TryGetValue(keySpan, out var emojiPath))
            {
                emojiImage = null;
                return false;
            }

            lock (_emojiCache)
            {
                var cacheLookup = _emojiCache.GetAlternateLookup<ReadOnlySpan<char>>();
                if (cacheLookup.TryGetValue(keySpan, out emojiImage))
                {
                    return emojiImage is not null;
                }

                using var fs = File.OpenRead(emojiPath);
                using var bitmap = SKBitmap.Decode(fs);
                if (bitmap is null)
                {
                    _progress.LogVerbose($"Failed to decode emoji {Path.GetFileName(emojiPath)}, skipping.");
                    emojiImage = null;
                    return false;
                }

                SKImageInfo oldEmojiInfo = bitmap.Info;
                SKImageInfo imageInfo = new SKImageInfo((int)(_emojiTargetHeight / (double)oldEmojiInfo.Height * oldEmojiInfo.Width), _emojiTargetHeight);
                using var newBitmap = new SKBitmap(imageInfo);
                bitmap.ScalePixels(newBitmap, SKFilterQuality.High);
                newBitmap.SetImmutable();

                emojiImage = SKImage.FromBitmap(newBitmap);
                _emojiCache[keySpan.ToString()] = emojiImage;
                return true;
            }
        }

        private void DrawEmojiMessage(List<SectionImage> sectionImages, List<EmotePosition> emotePositionList, ref Point drawPos, Point defaultPos, int bitsCount, ReadOnlySpan<char> fragment, bool highlightWords, SectionRenderContext ctx, int firstEmoji = -1)
        {
            var emojiMatches = new List<string>();
            Span<char> stackSpace = stackalloc char[16];

            var fragmentSlice = fragment;
            var nonEmojiStart = 0;
            var nonEmojiLen = 0;
            if (firstEmoji > 0)
            {
                nonEmojiLen = firstEmoji;
                fragmentSlice = fragmentSlice[firstEmoji..];
            }

            while (true)
            {
                var elementLength = StringInfo.GetNextTextElementLength(fragmentSlice);
                if (elementLength == 0)
                {
                    break;
                }

                if (elementLength == 1 && char.IsAscii(fragmentSlice[0]))
                {
                    nonEmojiLen += elementLength;
                    fragmentSlice = fragmentSlice[elementLength..];
                    continue;
                }

                var textElement = fragmentSlice[..elementLength];
                fragmentSlice = fragmentSlice[elementLength..];

                var lookupKey = elementLength <= stackSpace.Length ? stackSpace : new char[elementLength];
                var written = textElement.CopyToExcept(stackSpace, EmojiExcludeChars);
                if (!TryGetEmojiImage(lookupKey[..written], out var emojiImage))
                {
                    emojiImage = SplitZwjEmoji(sectionImages, emotePositionList, ref drawPos, defaultPos, bitsCount, highlightWords, textElement, ctx);
                    emojiImage ??= LookupEmojiSlow(textElement, emojiMatches);
                    if (emojiImage is null)
                    {
                        nonEmojiLen += elementLength;
                        continue;
                    }
                }

                if (nonEmojiLen > 0)
                {
                    DrawFragmentPart(sectionImages, emotePositionList, ref drawPos, defaultPos, bitsCount, fragment.Slice(nonEmojiStart, nonEmojiLen), highlightWords, ctx, true, true);
                    nonEmojiStart += nonEmojiLen;
                    nonEmojiLen = 0;
                }

                SKImageInfo emojiImageInfo = emojiImage.Info;
                if (drawPos.X + emojiImageInfo.Width > renderOptions.ChatWidth - renderOptions.SidePadding * 2)
                {
                    AddImageSection(sectionImages, ref drawPos, defaultPos);
                }

                Point emotePoint = new Point
                {
                    X = drawPos.X + (int)Math.Ceiling(renderOptions.EmoteSpacing / 2d), // emotePoint.X halfway through emote padding
                    Y = (int)((renderOptions.SectionHeight - emojiImageInfo.Height) / 2.0)
                };

                var canvas = sectionImages[^1].Canvas;
                if (highlightWords)
                {
                    var paint = GetCachedPaint(Purple, ctx.PaintCache);
                    canvas.DrawRect((int)(emotePoint.X - renderOptions.EmoteSpacing / 2d), 0, emojiImageInfo.Width + renderOptions.EmoteSpacing, renderOptions.SectionHeight, paint);
                }

                canvas.DrawImage(emojiImage, emotePoint.X, emotePoint.Y);
                nonEmojiStart += elementLength;

                drawPos.X += emojiImageInfo.Width + renderOptions.EmoteSpacing;
            }

            if (nonEmojiLen > 0)
            {
                DrawFragmentPart(sectionImages, emotePositionList, ref drawPos, defaultPos, bitsCount, fragment.Slice(nonEmojiStart, nonEmojiLen), highlightWords, ctx, true, true);
            }
        }

        [return: MaybeNull]
        private SKImage SplitZwjEmoji(List<SectionImage> sectionImages, List<EmotePosition> emotePositionList, ref Point drawPos, Point defaultPos, int bitsCount, bool highlightWords, ReadOnlySpan<char> textElement, SectionRenderContext ctx)
        {
            Span<char> stackSpace = stackalloc char[16];

            if (!textElement.Contains(ZERO_WIDTH_JOINER))
            {
                return null;
            }

            ReadOnlySpan<char> subEmoji = default;
            foreach (var range in textElement.Split(ZERO_WIDTH_JOINER))
            {
                if (!subEmoji.IsEmpty)
                {
                    DrawEmojiMessage(sectionImages, emotePositionList, ref drawPos, defaultPos, bitsCount, subEmoji, highlightWords, ctx);
                }

                subEmoji = textElement[range];
            }

            var lookupKey = subEmoji.Length <= stackSpace.Length ? stackSpace : new char[subEmoji.Length];
            var written = subEmoji.CopyToExcept(lookupKey, EmojiExcludeChars);
            return TryGetEmojiImage(lookupKey[..written], out var emojiImage)
                ? emojiImage
                : null;
        }

        [return: MaybeNull]
        private SKImage LookupEmojiSlow(ReadOnlySpan<char> textElement, List<string> emojiMatches)
        {
            var firstCodepoint = textElement.Length > 1 && char.IsHighSurrogate(textElement[0]) && char.IsLowSurrogate(textElement[1])
                ? char.ConvertToUtf32(textElement[0], textElement[1])
                : textElement[0];

            emojiMatches.Clear();
            if (AllEmojiSequences.TryGetValue(firstCodepoint, out var matches))
            {
                foreach (var emoji in matches)
                {
                    if (textElement.StartsWith(emoji))
                    {
                        emojiMatches.Add(emoji);
                    }
                }
            }

            var emojiMatchesCount = emojiMatches.Count;
            if (emojiMatchesCount == 0)
            {
                return null;
            }

            var selectedEmoji = emojiMatches.MaxBy(x => x.Length);
            return TryGetEmojiImage(selectedEmoji, out var emojiImage) ? emojiImage : null;
        }

        private void DrawNonFontMessage(List<SectionImage> sectionImages, ref Point drawPos, Point defaultPos, ReadOnlySpan<char> fragment, bool highlightWords, SectionRenderContext ctx)
        {
            fragment = fragment.Trim('\uFE0F');

            if (fragment.ContainsAnyInRange('\u2500', '\u28FF') && BlockArtRegex.IsMatch(fragment))
            {
                // Very rough estimation of width of block art
                var textWidth = (int)(fragment.Length * renderOptions.BlockArtCharWidth);
                if (renderOptions.BlockArtPreWrap && drawPos.X + textWidth > renderOptions.BlockArtPreWrapWidth)
                {
                    AddImageSection(sectionImages, ref drawPos, defaultPos);
                }
            }

            // We cannot draw nonFont chars individually or Arabic script looks improper https://github.com/lay295/TwitchDownloader/issues/484
            // The fragment has either surrogate pairs or characters not in the messageFont
            var inFontBuffer = new StringBuilder();
            var nonFontBuffer = new StringBuilder();
            for (var j = 0; j < fragment.Length; j++)
            {
                if (char.IsHighSurrogate(fragment[j]) && j + 1 < fragment.Length && char.IsLowSurrogate(fragment[j + 1]))
                {
                    if (inFontBuffer.Length > 0)
                    {
                        DrawText(inFontBuffer.ToString(), ctx.MessageFont, false, sectionImages, ref drawPos, defaultPos, highlightWords, ctx);
                        inFontBuffer.Clear();
                    }
                    if (nonFontBuffer.Length > 0)
                    {
                        var nonFontFallbackFont = GetFallbackFont(nonFontBuffer[0], ctx.FallbackFontCache);
                        nonFontFallbackFont.Color = renderOptions.MessageColor;
                        DrawText(nonFontBuffer.ToString(), nonFontFallbackFont, false, sectionImages, ref drawPos, defaultPos, highlightWords, ctx);
                        nonFontBuffer.Clear();
                    }

                    var utf32Char = char.ConvertToUtf32(fragment[j], fragment[j + 1]);
                    //Don't attempt to draw U+E0000
                    if (utf32Char != 0xE0000)
                    {
                        var highSurrogateFallbackFont = GetFallbackFont(utf32Char, ctx.FallbackFontCache);
                        highSurrogateFallbackFont.Color = renderOptions.MessageColor;
                        DrawText(fragment.Slice(j, 2), highSurrogateFallbackFont, false, sectionImages, ref drawPos, defaultPos, highlightWords, ctx);
                    }
                    j++;
                }
                else if (!ctx.MessageFont.ContainsGlyphs(fragment.Slice(j, 1)) || fragment.Slice(j, 1).LengthInTextElements() == 0)
                {
                    if (inFontBuffer.Length > 0)
                    {
                        DrawText(inFontBuffer.ToString(), ctx.MessageFont, false, sectionImages, ref drawPos, defaultPos, highlightWords, ctx);
                        inFontBuffer.Clear();
                    }

                    nonFontBuffer.Append(fragment[j]);
                }
                else
                {
                    if (nonFontBuffer.Length > 0)
                    {
                        var fallbackFont = GetFallbackFont(nonFontBuffer[0], ctx.FallbackFontCache);
                        fallbackFont.Color = renderOptions.MessageColor;
                        DrawText(nonFontBuffer.ToString(), fallbackFont, false, sectionImages, ref drawPos, defaultPos, highlightWords, ctx);
                        nonFontBuffer.Clear();
                    }

                    inFontBuffer.Append(fragment[j]);
                }
            }

            // Only one or the other should occur
            if (nonFontBuffer.Length > 0)
            {
                var fallbackFont = GetFallbackFont(nonFontBuffer[0], ctx.FallbackFontCache);
                fallbackFont.Color = renderOptions.MessageColor;
                DrawText(nonFontBuffer.ToString(), fallbackFont, true, sectionImages, ref drawPos, defaultPos, highlightWords, ctx);
                nonFontBuffer.Clear();
            }
            if (inFontBuffer.Length > 0)
            {
                DrawText(inFontBuffer.ToString(), ctx.MessageFont, true, sectionImages, ref drawPos, defaultPos, highlightWords, ctx);
                inFontBuffer.Clear();
            }
        }

        private bool TryDrawBits(List<SectionImage> sectionImages, List<EmotePosition> emotePositionList, ref Point drawPos, Point defaultPos, int bitsCount, ReadOnlySpan<char> fragmentString)
        {
            if (bitsCount < 1)
            {
                Debug.Fail($"Check {nameof(bitsCount)} before calling TryDrawBits to avoid setting up the method call.");
                return false;
            }

            var bitsIndex = fragmentString.IndexOfAny(DigitChars);
            var cheermoteLookup = _cheermoteCache.GetAlternateLookup<ReadOnlySpan<char>>();
            if (bitsIndex > 0 && int.TryParse(fragmentString[bitsIndex..], out var bitsAmount) && cheermoteLookup.TryGetValue(fragmentString[..bitsIndex], out var currentCheerEmote))
            {
                var tieredEmote = currentCheerEmote.GetTier(bitsAmount).Value;
                var emoteImageInfo = tieredEmote.Info;
                if (drawPos.X + emoteImageInfo.Width > renderOptions.ChatWidth - renderOptions.SidePadding * 2)
                {
                    AddImageSection(sectionImages, ref drawPos, defaultPos);
                }

                var emotePoint = new Point
                {
                    X = drawPos.X,
                    Y = (int)((sectionImages.Count - 1) * renderOptions.SectionHeight + ((renderOptions.SectionHeight - emoteImageInfo.Height) / 2.0))
                };
                emotePositionList.Add(new EmotePosition(emotePoint, tieredEmote));
                drawPos.X += emoteImageInfo.Width + renderOptions.EmoteSpacing;

                return true;
            }

            return false;
        }

        private void DrawFirstPartyEmote(List<SectionImage> sectionImages, List<EmotePosition> emotePositionList, ref Point drawPos, Point defaultPos, Fragment fragment, bool highlightWords, SectionRenderContext ctx)
        {
            // First party emote
            var emoteLookup = _emoteCache.GetAlternateLookup<ReadOnlySpan<char>>();
            if (emoteLookup.TryGetValue(fragment.emoticon.emoticon_id, out var emote))
            {
                SKImageInfo emoteInfo = emote.Info;
                if (drawPos.X + emoteInfo.Width > renderOptions.ChatWidth - renderOptions.SidePadding * 2)
                {
                    AddImageSection(sectionImages, ref drawPos, defaultPos);
                }
                Point emotePoint = new Point
                {
                    X = drawPos.X,
                    Y = (int)((sectionImages.Count - 1) * renderOptions.SectionHeight + ((renderOptions.SectionHeight - emoteInfo.Height) / 2.0))
                };

                if (highlightWords)
                {
                    var canvas = sectionImages[^1].Canvas;
                    var paint = GetCachedPaint(Purple, ctx.PaintCache);
                    canvas.DrawRect(drawPos.X, 0, emoteInfo.Width + renderOptions.EmoteSpacing, renderOptions.SectionHeight, paint);
                }

                emotePositionList.Add(new EmotePosition(emotePoint, emote));
                drawPos.X += emoteInfo.Width + renderOptions.EmoteSpacing;
            }
            else
            {
                // Probably an old emote that was removed
                DrawText(fragment.text, ctx.MessageFont, true, sectionImages, ref drawPos, defaultPos, highlightWords, ctx);
            }
        }

        private static readonly SearchValues<char> DrawTextDelimiters = SearchValues.Create("?-");

        private void DrawText(ReadOnlySpan<char> drawText, SKPaint textFont, bool padding, List<SectionImage> sectionImages, ref Point drawPos, Point defaultPos, bool highlightWords, SectionRenderContext ctx, bool noWrap = false)
        {
            bool isRtl = IsRightToLeft(drawText);
            float textWidth = MeasureText(drawText, textFont, isRtl);
            int effectiveChatWidth = renderOptions.ChatWidth - renderOptions.SidePadding - defaultPos.X;

            while (!noWrap && textWidth > effectiveChatWidth && !drawText.IsEmpty)
            {
                var newDrawText = SubstringToTextWidth(drawText, textFont, effectiveChatWidth, isRtl, DrawTextDelimiters);
                var overrideWrap = false;

                if (newDrawText.Length == 0)
                {
                    // When chat width is small enough and font size is big enough, 1 character can be wider than effectiveChatWidth.
                    overrideWrap = true;
                    var charCount = Math.Max(StringInfo.GetNextTextElementLength(drawText), 1);
                    newDrawText = drawText[..charCount];
                }

                DrawText(newDrawText, textFont, padding, sectionImages, ref drawPos, defaultPos, highlightWords, ctx, overrideWrap);

                drawText = drawText[newDrawText.Length..];
                textWidth = MeasureText(drawText, textFont, isRtl);
            }
            if (drawPos.X + textWidth > renderOptions.ChatWidth - renderOptions.SidePadding * 2)
            {
                AddImageSection(sectionImages, ref drawPos, defaultPos);
            }

            var canvas = sectionImages[^1].Canvas;
            if (highlightWords)
            {
                var paint = GetCachedPaint(Purple, ctx.PaintCache);
                canvas.DrawRect(drawPos.X, 0, textWidth + (padding ? renderOptions.WordSpacing : 0), renderOptions.SectionHeight, paint);
            }

            if (renderOptions.Outline)
            {
                using var outlinePath = isRtl
                    ? textFont.GetShapedTextPath(drawText, drawPos.X, drawPos.Y)
                    : textFont.GetTextPath(drawText, drawPos.X, drawPos.Y);

                canvas.DrawPath(outlinePath, ctx.OutlinePaint);
            }

            if (drawText.ContainsAnyInRange('\u0591', '\uFEFC') && RtlRegex.IsMatch(drawText))
            {
                canvas.DrawShapedText(drawText.ToString(), drawPos.X, drawPos.Y, textFont);
            }
            else
            {
                canvas.DrawText(drawText, drawPos.X, drawPos.Y, textFont);
            }

            drawPos.X += (int)Math.Floor(textWidth + (padding ? renderOptions.WordSpacing : 0));
        }

        /// <summary>
        /// Produces a <see langword="string"/> less than or equal to <paramref name="maxWidth"/> when drawn with <paramref name="textFont"/> OR substringed to the last index of any character in <paramref name="delimiters"/>.
        /// </summary>
        /// <returns>A shortened in visual width or delimited <see langword="string"/>, whichever comes first.</returns>
        private static ReadOnlySpan<char> SubstringToTextWidth(ReadOnlySpan<char> text, SKPaint textFont, int maxWidth, bool isRtl, SearchValues<char> delimiters)
        {
            // If we are dealing with non-RTL and don't have any delimiters then SKPaint.BreakText is over 9x faster
            if (!isRtl && text.IndexOfAny(delimiters) == -1)
            {
                return SubstringToTextWidth(text, textFont, maxWidth);
            }

            using var shaper = isRtl
                ? new SKShaper(textFont.Typeface)
                : null;

            // Input text was already less than max width
            if (MeasureText(text, textFont, isRtl, shaper) <= maxWidth)
            {
                return text;
            }

            // Cut in half until <= width
            var length = text.Length;
            do
            {
                length /= 2;
            }
            while (MeasureText(text[..length], textFont, isRtl, shaper) > maxWidth);

            // Add chars until greater than width, then remove the last
            do
            {
                length++;
            } while (MeasureText(text[..length], textFont, isRtl, shaper) < maxWidth);
            text = text[..(length - 1)];

            // Cut at the last delimiter character if applicable
            var delimiterIndex = text.LastIndexOfAny(delimiters);
            if (delimiterIndex != -1)
            {
                return text[..(delimiterIndex + 1)];
            }

            return text;
        }

        /// <summary>
        /// Produces a <see cref="ReadOnlySpan{T}"/> less than or equal to <paramref name="maxWidth"/> when drawn with <paramref name="textFont"/>
        /// </summary>
        /// <returns>A shortened in visual width <see cref="ReadOnlySpan{T}"/>.</returns>
        /// <remarks>This is not compatible with text that needs to be shaped.</remarks>
        private static ReadOnlySpan<char> SubstringToTextWidth(ReadOnlySpan<char> text, SKPaint textFont, int maxWidth)
        {
            var length = (int)textFont.BreakText(text, maxWidth);
            return text[..length];
        }

        private static float MeasureText(ReadOnlySpan<char> text, SKPaint textFont, bool? isRtl = null, SKShaper shaper = null)
        {
            isRtl ??= IsRightToLeft(text);

            if (isRtl == false)
            {
                return textFont.MeasureText(text);
            }

            if (shaper == null)
            {
                return MeasureRtlText(text, textFont);
            }

            return MeasureRtlText(text, textFont, shaper);
        }

        private static float MeasureRtlText(ReadOnlySpan<char> rtlText, SKPaint textFont)
        {
            using var shaper = new SKShaper(textFont.Typeface);
            return MeasureRtlText(rtlText, textFont, shaper);
        }

        private static float MeasureRtlText(ReadOnlySpan<char> rtlText, SKPaint textFont, SKShaper shaper)
        {
            using var buffer = new HarfBuzzSharp.Buffer();
            buffer.Add(rtlText, textFont.TextEncoding);
            SKShaper.Result measure = shaper.Shape(buffer, textFont);
            return measure.Width;
        }

        private void DrawUsername(Comment comment, List<SectionImage> sectionImages, ref Point drawPos, Point defaultPos, SectionRenderContext ctx, bool appendColon = true, SKColor? colorOverride = null, int commentIndex = 0)
        {
            var userColor = colorOverride ?? (comment.message.user_color is not null
                ? SKColor.Parse(comment.message.user_color)
                : DefaultUsernameColors[Math.Abs(comment.commenter.display_name.GetHashCode()) % DefaultUsernameColors.Length]);

            if (colorOverride is null && renderOptions.AdjustUsernameVisibility)
            {
                var backgroundColor = GetMessageBackground(commentIndex, ctx.PaintCache, out _);
                userColor = AdjustUsernameVisibility(userColor, backgroundColor, ctx.OutlinePaint);
            }

            var userPaint = comment.commenter.display_name.Any(IsNotAscii)
                ? GetFallbackFont(comment.commenter.display_name.First(IsNotAscii), ctx.FallbackFontCache)
                : ctx.NameFont;

            userPaint.Color = userColor;
            var userName = appendColon
                ? comment.commenter.display_name + ":"
                : comment.commenter.display_name;

            // Center the username using its own font metrics so a scaled username font stays vertically
            // centered within the section, then restore drawPos.Y for the message text that follows.
            int savedY = drawPos.Y;
            drawPos.Y = _usernameCenteredY;
            DrawText(userName, userPaint, true, sectionImages, ref drawPos, defaultPos, false, ctx);
            drawPos.Y = savedY;
        }

        private SKColor AdjustUsernameVisibility(SKColor userColor, SKColor backgroundColor, SKPaint currentOutlinePaint)
        {
            const byte OPAQUE_THRESHOLD = byte.MaxValue / 2;
            if (!renderOptions.Outline && backgroundColor.Alpha < OPAQUE_THRESHOLD)
            {
                // Background lightness cannot be truly known.
                return userColor;
            }

            var newUserColor = AdjustColorVisibility(userColor, renderOptions.Outline ? currentOutlinePaint.Color : backgroundColor);

            return renderOptions.Outline || backgroundColor.Alpha == byte.MaxValue
                ? newUserColor
                : userColor.Lerp(newUserColor, (float)backgroundColor.Alpha / byte.MaxValue);
        }

        private static SKColor AdjustColorVisibility(SKColor foreground, SKColor background)
        {
            background.ToHsl(out var bgHue, out var bgSat, out _);
            foreground.ToHsl(out var fgHue, out var fgSat, out var fgLight);

            // Adjust lightness
            if (background.RelativeLuminance() > 0.5)
            {
                // Bright background
                if (fgLight > 60)
                {
                    fgLight = 60;
                }

                if (bgSat <= 28)
                {
                    fgHue = fgHue switch
                    {
                        > 48 and < 90 => AdjustHue(fgHue, 48, 90), // Yellow-Lime
                        > 164 and < 186 => AdjustHue(fgHue, 164, 186), // Turquoise
                        _ => fgHue
                    };
                }
            }
            else
            {
                // Dark background
                if (fgLight < 40)
                {
                    fgLight = 40;
                }

                if (bgSat <= 28)
                {
                    fgHue = fgHue switch
                    {
                        > 224 and < 263 => AdjustHue(fgHue, 224, 264), // Blue-Purple
                        _ => fgHue
                    };
                }
            }

            // Adjust hue on colored backgrounds
            if (bgSat > 28 && fgSat > 28)
            {
                const float HUE_WIDTH = 360;
                const int ADJUST_THRESHOLD = 35;
                Debug.Assert(ADJUST_THRESHOLD < HUE_WIDTH / 2);

                // Compute computer lower and higher hue diff to ensure we wrap around for reds
                //       hue:   [||||||||||]
                //   not red:   [    ^     ]    ^
                // upper red: ^ [        ^ ]
                // lower red:   [^         ]^
                var hueDiff1 = fgHue - (bgHue > HUE_WIDTH / 2 ? bgHue - HUE_WIDTH : bgHue);
                var hueDiff2 = fgHue - (bgHue > HUE_WIDTH / 2 ? bgHue : bgHue + HUE_WIDTH);

                // Take smallest diff, or skip if both are >ADJUST_THRESHOLD
                float hueDiff;
                if (Math.Abs(hueDiff1) <= ADJUST_THRESHOLD) hueDiff = hueDiff1;
                else if (Math.Abs(hueDiff2) <= ADJUST_THRESHOLD) hueDiff = hueDiff2;
                else goto SkipHueAdjust;

                var diffSign = hueDiff < 0 ? -1 : 1; // Math.Sign returns 1, -1, or 0. We only want 1 or -1.
                fgHue = bgHue + ADJUST_THRESHOLD * diffSign;

                if (fgHue < 0) fgHue += HUE_WIDTH;
                fgHue %= HUE_WIDTH;

                SkipHueAdjust:;
            }

            return SKColor.FromHsl(fgHue, Math.Min(fgSat, 90), fgLight);

            static float AdjustHue(float hue, float lowerClamp, float upperClamp)
            {
                var midpoint = (upperClamp + lowerClamp) / 2;
                return hue >= midpoint ? upperClamp : lowerClamp;
            }
        }

        private void DrawAvatar(Comment comment, List<SectionImage> sectionImages, ref Point drawPos)
        {
            var avatarUrl = comment.commenter.logo;

            if (string.IsNullOrWhiteSpace(avatarUrl) || !_avatarCache.TryGetValue(avatarUrl, out var avatarImage))
            {
                avatarUrl = DefaultAvatarUrls[Math.Abs(comment.commenter.display_name.GetHashCode()) % DefaultAvatarUrls.Length];
                if (!_avatarCache.TryGetValue(avatarUrl, out avatarImage))
                {
                    return;
                }
            }

            var canvas = sectionImages[^1].Canvas;

            var avatarY = (float)((renderOptions.SectionHeight - avatarImage.Height) / 2.0);
            canvas.DrawImage(avatarImage, drawPos.X, avatarY);
            drawPos.X += avatarImage.Width + renderOptions.WordSpacing;
        }

        private void DrawBadges(Comment comment, List<SectionImage> sectionImages, ref Point drawPos)
        {
            if (comment.message.user_badges == null)
            {
                return;
            }

            var canvas = sectionImages[^1].Canvas;
            foreach (var badge in comment.message.user_badges)
            {
                if (!_badgeCache.TryGetValue(badge._id, out var cachedBadge))
                    continue;

                if ((renderOptions.ChatBadgeMask & cachedBadge.Type) != 0)
                    continue;

                if (!cachedBadge.Versions.TryGetValue(badge.version, out var badgeImage))
                    continue;

                float badgeY = (float)((renderOptions.SectionHeight - badgeImage.Height) / 2.0);
                canvas.DrawImage(badgeImage, drawPos.X, badgeY);
                drawPos.X += badgeImage.Width + renderOptions.WordSpacing / 2;
            }
        }

        private void DrawTimestamp(Comment comment, List<SectionImage> sectionImages, ref Point drawPos, ref Point defaultPos, SectionRenderContext ctx)
        {
            var canvas = sectionImages[^1].Canvas;
            var timestamp = new TimeSpan(0, 0, (int)comment.content_offset_seconds);

            const int MAX_TIMESTAMP_LENGTH = 8; // 48:00:00
            Span<char> timestampStackSpace = stackalloc char[MAX_TIMESTAMP_LENGTH];
            ReadOnlySpan<char> formattedTimestamp = timestamp.Ticks switch
            {
                >= 24 * TimeSpan.TicksPerHour => TimeSpanHFormat.ReusableInstance.Format(@"HH\:mm\:ss", timestamp),
                >= 1 * TimeSpan.TicksPerHour => timestamp.TryFormat(timestampStackSpace, out var charsWritten, @"h\:mm\:ss")
                    ? timestampStackSpace[..charsWritten]
                    : timestamp.ToString(@"h\:mm\:ss"),
                _ => timestamp.TryFormat(timestampStackSpace, out var charsWritten, @"m\:ss")
                    ? timestampStackSpace[..charsWritten]
                    : timestamp.ToString(@"m\:ss")
            };

            if (renderOptions.Outline)
            {
                using var outlinePath = ctx.MessageFont.GetTextPath(formattedTimestamp, drawPos.X, drawPos.Y);
                canvas.DrawPath(outlinePath, ctx.OutlinePaint);
            }

            canvas.DrawText(formattedTimestamp, drawPos.X, drawPos.Y, ctx.MessageFont);

            // We use pre-defined widths so all timestamps have the same defaultPos regardless of individual character width
            var textWidth = timestamp.Ticks switch
            {
                >= 10 * TimeSpan.TicksPerHour => renderOptions.TimestampWidths[3],
                >= 1 * TimeSpan.TicksPerHour => renderOptions.TimestampWidths[2],
                >= 10 * TimeSpan.TicksPerMinute => renderOptions.TimestampWidths[1],
                _ => renderOptions.TimestampWidths[0]
            };
            drawPos.X += textWidth + renderOptions.WordSpacing * 2;
            defaultPos.X = drawPos.X;
        }

        private void AddImageSection(List<SectionImage> sectionImages, ref Point drawPos, Point defaultPos)
        {
            drawPos.X = defaultPos.X;
            drawPos.Y = defaultPos.Y;

            sectionImages.Add(_sectionImageCache.Rent(renderOptions.ChatWidth, renderOptions.SectionHeight));
        }

        /// <summary>
        /// Fetches the emotes/badges/bits/emojis needed to render scaled to 2x
        /// </summary>
        /// <remarks>chatRoot.embeddedData will be empty after calling this to save on memory!</remarks>
        private async Task FetchScaledImages(CancellationToken cancellationToken)
        {
            var badgeTask = GetScaledBadges(cancellationToken);
            var emoteTask = GetScaledEmotes(cancellationToken);
            var emoteThirdTask = GetScaledThirdEmotes(cancellationToken);
            var cheerTask = GetScaledBits(cancellationToken);
            var emojiTask = GetEmojiIndexAsync(cancellationToken);
            var avatarTask = renderOptions.RenderUserAvatars ? GetScaledAvatars(cancellationToken) : Task.FromResult(new Dictionary<string, SKImage>());
            var gifTask = renderOptions.GiphyGifs ? GetScaledGiphyGifs(cancellationToken) : Task.FromResult(new List<TwitchEmote>());

            await Task.WhenAll(badgeTask, emoteTask, emoteThirdTask, cheerTask, emojiTask, avatarTask, gifTask);

            // Clear chatRoot.embeddedData and trigger a single GC only if embedded data was present
            if (chatRoot.embeddedData is not null)
            {
                chatRoot.embeddedData = null;
                GC.Collect();
            }

            _badgeCache.AddRange(badgeTask.Result, x => x.Name, x => x);
            _emoteCache.AddRange(emoteTask.Result, x => x.Id, x => x);
            _emoteThirdCache.AddRange(emoteThirdTask.Result, x => x.Name, x => x);
            _cheermoteCache.AddRange(cheerTask.Result, x => x.prefix, x => x);
            _emojiPaths = emojiTask.Result;
            _avatarCache.AddRange(avatarTask.Result);
            _gifCache.AddRange(gifTask.Result, x => x.Name, x => x);
        }

        private async Task<List<ChatBadge>> GetScaledBadges(CancellationToken cancellationToken)
        {
            // Do not fetch if badges are disabled
            if (!renderOptions.ChatBadges)
            {
                return [];
            }

            var badgeTask = await TwitchHelper.GetChatBadges(chatRoot.comments, chatRoot.streamer.id, _cacheDir, _progress, chatRoot.embeddedData, renderOptions.Offline, cancellationToken);

            var newHeight = (int)Math.Round(36 * renderOptions.ReferenceScale * renderOptions.BadgeScale);
            var snapThreshold = (int)Math.Round(1 * renderOptions.ReferenceScale);
            foreach (var badge in badgeTask)
            {
                cancellationToken.ThrowIfCancellationRequested();

                badge.SnapResize(newHeight, snapThreshold, snapThreshold);
            }

            return badgeTask;
        }

        private async Task<List<TwitchEmote>> GetScaledEmotes(CancellationToken cancellationToken)
        {
            var emoteTask = await TwitchHelper.GetEmotes(chatRoot.comments, _cacheDir, _progress, chatRoot.embeddedData, renderOptions.Offline, cancellationToken);

            var snapThreshold = (int)Math.Round(4 * renderOptions.ReferenceScale);
            foreach (var emote in emoteTask)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var newScale = 2.0 / emote.ImageScale * renderOptions.ReferenceScale * renderOptions.EmoteScale;
                emote.SnapScale(newScale, snapThreshold, snapThreshold);
            }

            return emoteTask;
        }

        private async Task<List<TwitchEmote>> GetScaledThirdEmotes(CancellationToken cancellationToken)
        {
            var emoteThirdTask = await TwitchHelper.GetThirdPartyEmotes(chatRoot.comments, chatRoot.streamer.id, _cacheDir, _progress, chatRoot.embeddedData, renderOptions.BttvEmotes, renderOptions.FfzEmotes,
                renderOptions.StvEmotes, renderOptions.AllowUnlistedEmotes, renderOptions.Offline, cancellationToken);

            var snapThreshold = (int)Math.Round(4 * renderOptions.ReferenceScale);
            foreach (var emote in emoteThirdTask)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var newScale = 2.0 / emote.ImageScale * renderOptions.ReferenceScale * renderOptions.EmoteScale;
                emote.SnapScale(newScale, snapThreshold, snapThreshold);
            }

            return emoteThirdTask;
        }

        private async Task<List<TwitchEmote>> GetScaledGiphyGifs(CancellationToken cancellationToken)
        {
            List<TwitchEmote> gifTask;
            try
            {
                gifTask = await TwitchHelper.GetGiphyGifs(chatRoot.comments, _cacheDir, _progress, chatRoot.embeddedData, renderOptions.Offline, cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Chat GIFs must never cost the user their render, the messages just stay as plain text
                _progress.LogWarning($"Unable to load chat GIFs: {ex.Message} Rendering them as text instead.");
                return [];
            }

            var maxWidth = renderOptions.ChatWidth - renderOptions.SidePadding * 2;
            // Purely a readability choice: an oversized message is clipped at the top of the frame rather than
            // breaking anything, so this only decides how much of the chat one GIF is allowed to take up.
            var maxHeight = Math.Max(1, renderOptions.ChatHeight * Math.Max(1, renderOptions.GifMaxHeightPercent) / 100);
            foreach (var gif in gifTask)
            {
                cancellationToken.ThrowIfCancellationRequested();

                gif.Scale(Math.Min(maxWidth / (double)gif.Width, maxHeight / (double)gif.Height));
            }

            return gifTask;
        }

        // By reference, not name: first party emotes carry a null name, and one could share a GIF's title
        private static void PopulateOnScreenImages(List<CommentSection> onScreen, Dictionary<int, CommentSection> preRendered, HashSet<TwitchEmote> destination)
        {
            destination.Clear();
            foreach (var comment in onScreen)
            {
                foreach (var (_, emote) in comment.Emotes)
                {
                    destination.Add(emote);
                }
            }

            foreach (var preRenderedComment in preRendered.Values)
            {
                if (preRenderedComment is null) continue;
                foreach (var (_, emote) in preRenderedComment.Emotes)
                {
                    destination.Add(emote);
                }
            }
        }

        private async Task<List<CheerEmote>> GetScaledBits(CancellationToken cancellationToken)
        {
            var cheerTask = await TwitchHelper.GetBits(chatRoot.comments, _cacheDir, chatRoot.streamer.id.ToString(), _progress, chatRoot.embeddedData, renderOptions.Offline, cancellationToken);

            var snapThreshold = (int)Math.Round(4 * renderOptions.ReferenceScale);
            foreach (var cheer in cheerTask)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var imageScale = cheer.tierList.FirstOrDefault().Value?.ImageScale ?? 2;
                var newScale = 2.0 / imageScale * renderOptions.ReferenceScale * renderOptions.EmoteScale;
                cheer.SnapScale(newScale, snapThreshold, snapThreshold);
            }

            return cheerTask;
        }

        /// <summary>
        /// Indexes all emoji sequence keys to their on-disk PNG paths without eagerly decoding or scaling all ~3,689 images.
        /// Individual emojis are decoded and scaled on demand when encountered in chat.
        /// </summary>
        private async Task<Dictionary<string, string>> GetEmojiIndexAsync(CancellationToken cancellationToken)
        {
            _emojiTargetHeight = (int)Math.Round(36 * renderOptions.ReferenceScale * renderOptions.EmojiScale);
            var emojiFiles = await TwitchHelper.GetEmojiFilesAsync(_cacheDir, renderOptions.EmojiVendor, cancellationToken);
            if (emojiFiles.Length == 0)
            {
                return [];
            }

            var index = new Dictionary<string, string>(emojiFiles.Length, StringComparer.Ordinal);
            var sb = new StringBuilder(8);

            foreach (var emojiPath in emojiFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var span = Path.GetFileNameWithoutExtension(emojiPath.AsSpan());
                sb.Clear();
                foreach (var range in span.SplitAny())
                {
                    var num = uint.Parse(span[range], NumberStyles.HexNumber);
                    if (num == 0xFE0F) continue;

                    sb.Append($"{new Rune(num)}");
                }

                index[sb.ToString()] = emojiPath;
            }

            return index;
        }

        private async Task<Dictionary<string, SKImage>> GetScaledAvatars(CancellationToken cancellationToken)
        {
            var avatars = await TwitchHelper.GetAvatars(chatRoot.comments, DefaultAvatarUrls, _cacheDir, _progress, renderOptions.Offline, cancellationToken);

            var newHeight = (int)Math.Round(36 * renderOptions.ReferenceScale * renderOptions.AvatarScale);

            using var maskPath = new SKPath();
            var radius = newHeight / 2;
            maskPath.AddCircle(radius, radius, radius);

            return avatars.Keys.ToDictionary(x => x, x =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var oldBitmap = avatars[x];
                var oldImageInfo = oldBitmap.Info;
                var imageInfo = new SKImageInfo((int)(newHeight / (double)oldImageInfo.Height * oldImageInfo.Width), newHeight);
                using var newBitmap = new SKBitmap(imageInfo);
                oldBitmap.ScalePixels(newBitmap, SKFilterQuality.High);

                // Clip avatar to circle
                using (var canvas = new SKCanvas(newBitmap))
                {
                    canvas.ClipPath(maskPath, SKClipOperation.Difference, true);
                    canvas.Clear();
                }

                newBitmap.SetImmutable();
                return SKImage.FromBitmap(newBitmap);
            });
        }

        private (int startTick, int totalTicks) GetVideoTicks()
        {
            if (renderOptions.StartOverride != -1 && renderOptions.EndOverride != -1)
            {
                int startSeconds = renderOptions.StartOverride;
                int videoStartTick = startSeconds * renderOptions.Framerate;
                int totalTicks = renderOptions.EndOverride * renderOptions.Framerate - videoStartTick;
                return (videoStartTick, totalTicks);
            }
            else
            {
                int startSeconds = (int)Math.Floor(chatRoot.video.start);
                int videoStartTick = startSeconds * renderOptions.Framerate;
                int totalTicks = (int)Math.Ceiling(chatRoot.video.end * renderOptions.Framerate) - videoStartTick;
                return (videoStartTick, totalTicks);
            }
        }

        private static SKPaint GetCachedPaint(SKColor color, DisposableDictionary<SKColor, SKPaint> paintCache)
        {
            ref var paint = ref CollectionsMarshal.GetValueRefOrAddDefault(paintCache, color, out var alreadyExists);
            if (alreadyExists)
            {
                return paint;
            }

            paint = new SKPaint { Color = color };
            return paint;
        }

        private SKPaint GetFallbackFont(int input, DisposableDictionary<int, SKPaint> fallbackCache)
        {
            ref var fallbackPaint = ref CollectionsMarshal.GetValueRefOrAddDefault(fallbackCache, input, out bool alreadyExists);
            if (alreadyExists)
            {
                return fallbackPaint;
            }

            SKPaint newPaint = new SKPaint() { Typeface = fontManager.MatchCharacter(input), LcdRenderText = true, TextSize = (float)renderOptions.FontSize, IsAntialias = true, SubpixelText = true, IsAutohinted = true, HintingLevel = SKPaintHinting.Full, FilterQuality = SKFilterQuality.High };
            if (newPaint.Typeface == null)
            {
                newPaint.Typeface = SKTypeface.Default;
                if (Interlocked.Exchange(ref _noFallbackFontLogged, 1) == 0)
                {
                    _progress.LogWarning("No valid typefaces were found for some messages.");
                    _progress.LogVerbose($"Could not find typeface for codepoint: {input}");
                }
            }

            fallbackPaint = newPaint;
            return newPaint;
        }

        private static bool IsNotAscii(char input)
        {
            return input > 127;
        }

        /// <summary>Sorts the word order of a given <paramref name="enumerator"/> by RTL rules.</summary>
        /// <param name="enumerator">A span enumerator.</param>
        /// <param name="destination">The RTL-swapped output from enumerating the <paramref name="enumerator"/>.</param>
        /// <returns>The number of words written.</returns>
        /// <exception cref="IndexOutOfRangeException"><paramref name="destination"/> was too small.</exception>
        private static int SwapRightToLeft(MemoryExtensions.SpanSplitEnumerator<char> enumerator, Span<Range> destination)
        {
            var source = enumerator.Source;
            var rtlStack = ArrayPool<Range>.Shared.Rent(destination.Length);
            var rtlStackPos = 0;
            var destPos = 0;

            try
            {
                foreach (var range in enumerator)
                {
                    if (range.Start.Value == range.End.Value) continue;

                    if (IsRightToLeft(source[range]))
                    {
                        rtlStack[rtlStackPos] = range;
                        rtlStackPos++;
                    }
                    else
                    {
                        EmptyRtlStack(rtlStack, ref rtlStackPos, destination, ref destPos);

                        destination[destPos] = range;
                        destPos++;
                    }
                }

                EmptyRtlStack(rtlStack, ref rtlStackPos, destination, ref destPos);

                return destPos;
            }
            finally
            {
                ArrayPool<Range>.Shared.Return(rtlStack);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void EmptyRtlStack(Span<Range> rtlStack, ref int rtlStackPos, Span<Range> destination, ref int destPos)
            {
                while (rtlStackPos > 0)
                {
                    destination[destPos] = rtlStack[rtlStackPos - 1];
                    rtlStackPos--;
                    destPos++;
                }
            }
        }

        private static bool IsRightToLeft(ReadOnlySpan<char> message)
        {
            if (message.Length > 0)
            {
                return message[0] >= '\u0591' && message[0] <= '\u07FF';
            }

            return false;
        }

        public async Task<ChatRoot> ParseJsonAsync(CancellationToken cancellationToken = new())
        {
            chatRoot = await ChatJson.DeserializeAsync(renderOptions.InputFile, true, false, true, cancellationToken);
            return chatRoot;
        }

        public void Dispose()
        {
            Dispose(true);
        }

        private void Dispose(bool isDisposing)
        {
            try
            {
                if (Disposed)
                {
                    return;
                }

                if (isDisposing)
                {
                    if (_workerContexts != null)
                    {
                        foreach (var ctx in _workerContexts.Values)
                        {
                            ctx?.Dispose();
                        }
                        _workerContexts.Dispose();
                    }

                    _badgeCache.Dispose();
                    _emoteCache.Dispose();
                    _emoteThirdCache.Dispose();
                    _gifCache.Dispose();
                    _cheermoteCache.Dispose();
                    _emojiCache.Dispose();
                    _avatarCache.Dispose();
                    _fallbackFontCache.Dispose();
                    _paintCache.Dispose();
                    _sectionImageCache?.Dispose();
                    fontManager?.Dispose();
                    nameFont?.Dispose();
                    messageFont?.Dispose();
                    outlinePaint?.Dispose();
                    highlightIcons?.Dispose();

                    _badgeCache.Clear();
                    _emoteCache.Clear();
                    _emoteThirdCache.Clear();
                    _gifCache.Clear();
                    _cheermoteCache.Clear();
                    _emojiCache.Clear();
                    _emojiPaths.Clear();
                    _avatarCache.Clear();
                    _fallbackFontCache.Clear();
                    _paintCache.Clear();

                    // Let the GC collect the caches immediately
                    chatRoot = null;
                    _badgeCache = null;
                    _emoteCache = null;
                    _emoteThirdCache = null;
                    _cheermoteCache = null;
                    _emojiCache = null;
                    _emojiPaths = null;
                    _avatarCache = null;
                    _fallbackFontCache = null;
                    _paintCache = null;
                }
            }
            finally
            {
                Disposed = true;
            }
        }
    }
}
