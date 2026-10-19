using SkiaSharp;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace TwitchDownloaderCore.TwitchObjects
{
    public enum EmoteProvider
    {
        FirstParty,
        ThirdParty,
        Giphy
    }

    // Frames are decoded lazily and can be released again: a long animated 7TV emote is a few hundred KB compressed
    // but tens of MB as bitmaps, and a busy channel has hundreds. Size and timings come from the codec header.
    [DebuggerDisplay("{Name}")]
    public sealed class TwitchEmote : IDisposable
    {
        private readonly object _decodeLock = new();
        public bool Disposed { get; private set; } = false;
        public SKCodec Codec { get; }
        public byte[] ImageData { get; set; }
        public EmoteProvider EmoteProvider { get; set; }

        private SKImage[] _emoteFrames;
        private SKImageInfo _info;
        private int[] _cumulativeDurationMs = [];

        public SKImage[] EmoteFrames
        {
            get
            {
                var frames = Volatile.Read(ref _emoteFrames);
                if (frames is not null)
                {
                    return frames;
                }

                lock (_decodeLock)
                {
                    return _emoteFrames ??= ExtractFrames();
                }
            }
        }

        public List<int> EmoteFrameDurations { get; private set; } = [];
        public int TotalDuration { get; set; }
        public string Name { get; }
        public string Id { get; }
        // Only set for images resolved at runtime, such as chat GIFs
        public string Url { get; set; }
        public int ImageScale { get; }
        public bool IsZeroWidth { get; set; }
        public int FrameCount { get; }
        public int Height => Info.Height;
        public int Width => Info.Width;

        // The size the emote draws at, known from the header without decoding a frame
        public SKImageInfo Info => _info;
        public bool FramesMaterialized => Volatile.Read(ref _emoteFrames) is not null;
        public long DecodedByteSize => (long)_info.Width * _info.Height * 4 * FrameCount;
        // Stamped by the renderer as it draws, so the least recently used can be released
        public long LastUsedTick { get; set; }

        public TwitchEmote(byte[] imageData, [AllowNull] SKCodec codec, EmoteProvider emoteProvider, int imageScale, string imageId, string imageName, bool isZeroWidth = false)
        {
            if (codec is null)
            {
                var ms = new MemoryStream(imageData);
                Codec = SKCodec.Create(ms, out var result);
                if (Codec is null)
                {
                    throw new Exception($"Skia was unable to decode {imageName} ({imageId}). Returned: {result}");
                }
            }
            else
            {
                Codec = codec;
            }

            EmoteProvider = emoteProvider;
            Id = imageId;
            Name = imageName;
            ImageScale = imageScale;
            ImageData = imageData;
            IsZeroWidth = isZeroWidth;
            FrameCount = Math.Max(1, Codec.FrameCount);

            var codecInfo = Codec.Info;
            _info = new SKImageInfo(codecInfo.Width, codecInfo.Height);

            CalculateDurations();
        }

        private void CalculateDurations()
        {
            EmoteFrameDurations = new List<int>(FrameCount);

            if (FrameCount == 1)
            {
                _cumulativeDurationMs = [100];
                return;
            }

            var frameInfos = Codec.FrameInfo;
            for (int i = 0; i < FrameCount; i++)
            {
                var duration = frameInfos[i].Duration / 10;
                EmoteFrameDurations.Add(duration);
                TotalDuration += duration;
            }

            if (TotalDuration == 0 || TotalDuration == FrameCount)
            {
                for (int i = 0; i < EmoteFrameDurations.Count; i++)
                {
                    EmoteFrameDurations[i] = 10;
                }
                TotalDuration = EmoteFrameDurations.Count * 10;
            }

            for (int i = 0; i < EmoteFrameDurations.Count; i++)
            {
                if (EmoteFrameDurations[i] == 0)
                {
                    TotalDuration += 10;
                    EmoteFrameDurations[i] = 10;
                }
            }

            _cumulativeDurationMs = new int[EmoteFrameDurations.Count];
            var runningMs = 0;
            for (int i = 0; i < EmoteFrameDurations.Count; i++)
            {
                runningMs += EmoteFrameDurations[i] * 10;
                _cumulativeDurationMs[i] = runningMs;
            }
        }

        /// <summary>
        /// Computes the active animation frame index for the given timestamp in milliseconds using O(log N) binary search.
        /// Matches the exact boundary semantics of the original linear subtraction loop.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetFrameIndex(long currentTickMs)
        {
            if (FrameCount <= 1 || TotalDuration <= 0)
            {
                return 0;
            }

            var imageFrame = (int)(currentTickMs % (TotalDuration * 10L));
            var cumulative = _cumulativeDurationMs;
            int lo = 0;
            int hi = cumulative.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (cumulative[mid] >= imageFrame)
                {
                    hi = mid;
                }
                else
                {
                    lo = mid + 1;
                }
            }

            return lo;
        }

        /// <summary>
        /// Computes the active animation frame index for <paramref name="currentTickMs"/> and outputs the exact millisecond
        /// timestamp <paramref name="nextChangeTickMs"/> at which this emote will advance to its next animation frame.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetFrameIndexAndNextChangeMs(long currentTickMs, out long nextChangeTickMs)
        {
            if (FrameCount <= 1 || TotalDuration <= 0)
            {
                nextChangeTickMs = long.MaxValue;
                return 0;
            }

            long totalDurationMs = TotalDuration * 10L;
            var imageFrame = (int)(currentTickMs % totalDurationMs);
            var cumulative = _cumulativeDurationMs;
            int lo = 0;
            int hi = cumulative.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (cumulative[mid] >= imageFrame)
                {
                    hi = mid;
                }
                else
                {
                    lo = mid + 1;
                }
            }

            long remainingMs = lo == cumulative.Length - 1
                ? totalDurationMs - imageFrame
                : (cumulative[lo] - imageFrame) + 1L;
            nextChangeTickMs = currentTickMs + remainingMs;
            return lo;
        }

        // Decodes every frame using PriorFrame chaining (O(N) instead of O(N^2) for delta GIF/WebP frames)
        // and scales multi-frame animations in parallel across CPU cores when needed.
        private SKImage[] ExtractFrames()
        {
            ObjectDisposedException.ThrowIf(Disposed, this);

            var codecInfo = Codec.Info;
            var nativeInfo = new SKImageInfo(codecInfo.Width, codecInfo.Height);
            var targetInfo = _info;
            var scaled = targetInfo.Width != codecInfo.Width || targetInfo.Height != codecInfo.Height;
            var frames = new SKImage[FrameCount];

            if (FrameCount == 1)
            {
                using var nativeBitmap = new SKBitmap(nativeInfo);
                Codec.GetPixels(nativeInfo, nativeBitmap.GetPixels(), new SKCodecOptions(0));

                if (!scaled)
                {
                    nativeBitmap.SetImmutable();
                    frames[0] = SKImage.FromBitmap(nativeBitmap);
                    return frames;
                }

                using var scaledBitmap = new SKBitmap(targetInfo);
                nativeBitmap.ScalePixels(scaledBitmap, SKFilterQuality.High);
                scaledBitmap.SetImmutable();
                frames[0] = SKImage.FromBitmap(scaledBitmap);
                return frames;
            }

            var frameInfos = Codec.FrameInfo;
            int priorFrame = -1;

            if (!scaled)
            {
                using var decodeScratch = new SKBitmap(nativeInfo);
                var byteCount = (long)nativeInfo.BytesSize;
                for (int i = 0; i < FrameCount; i++)
                {
                    DecodeFrameWithPrior(i, ref priorFrame, frameInfos, nativeInfo, decodeScratch);

                    using var frameBitmap = new SKBitmap(nativeInfo);
                    unsafe
                    {
                        Buffer.MemoryCopy((void*)decodeScratch.GetPixels(), (void*)frameBitmap.GetPixels(), byteCount, byteCount);
                    }
                    frameBitmap.SetImmutable();
                    frames[i] = SKImage.FromBitmap(frameBitmap);
                }

                return frames;
            }

            // When scaling a large multi-frame GIF/WebP (>= 8 frames), decode sequentially using PriorFrame chaining
            // and then scale all frames in parallel across CPU cores.
            if (FrameCount >= 8)
            {
                var nativeFrames = new SKBitmap[FrameCount];
                using (var decodeScratch = new SKBitmap(nativeInfo))
                {
                    var byteCount = (long)nativeInfo.BytesSize;
                    for (int i = 0; i < FrameCount; i++)
                    {
                        DecodeFrameWithPrior(i, ref priorFrame, frameInfos, nativeInfo, decodeScratch);

                        var copy = new SKBitmap(nativeInfo);
                        unsafe
                        {
                            Buffer.MemoryCopy((void*)decodeScratch.GetPixels(), (void*)copy.GetPixels(), byteCount, byteCount);
                        }
                        nativeFrames[i] = copy;
                    }
                }

                var maxParallel = Math.Clamp(Environment.ProcessorCount, 2, 16);
                Parallel.For(0, FrameCount, new ParallelOptions { MaxDegreeOfParallelism = maxParallel }, i =>
                {
                    using var src = nativeFrames[i];
                    using var dst = new SKBitmap(targetInfo);
                    src.ScalePixels(dst, SKFilterQuality.High);
                    dst.SetImmutable();
                    frames[i] = SKImage.FromBitmap(dst);
                });

                return frames;
            }

            using (var decodeScratch = new SKBitmap(nativeInfo))
            {
                for (int i = 0; i < FrameCount; i++)
                {
                    DecodeFrameWithPrior(i, ref priorFrame, frameInfos, nativeInfo, decodeScratch);

                    using var scaledBitmap = new SKBitmap(targetInfo);
                    decodeScratch.ScalePixels(scaledBitmap, SKFilterQuality.High);
                    scaledBitmap.SetImmutable();
                    frames[i] = SKImage.FromBitmap(scaledBitmap);
                }
            }

            return frames;
        }

        private void DecodeFrameWithPrior(int frameIndex, ref int priorFrame, SKCodecFrameInfo[] frameInfos, SKImageInfo nativeInfo, SKBitmap decodeScratch)
        {
            var ptr = decodeScratch.GetPixels();
            var canUsePrior = frameIndex > 0
                              && priorFrame == frameIndex - 1
                              && frameInfos is not null
                              && frameIndex < frameInfos.Length
                              && frameInfos[frameIndex].RequiredFrame == priorFrame
                              && frameInfos[priorFrame].DisposalMethod == SKCodecAnimationDisposalMethod.Keep;

            if (canUsePrior)
            {
                var result = Codec.GetPixels(nativeInfo, ptr, new SKCodecOptions(frameIndex, priorFrame));
                if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput)
                {
                    priorFrame = frameInfos[frameIndex].DisposalMethod == SKCodecAnimationDisposalMethod.RestorePrevious ? -1 : frameIndex;
                    return;
                }
            }

            Codec.GetPixels(nativeInfo, ptr, new SKCodecOptions(frameIndex));
            priorFrame = frameInfos is not null && frameIndex < frameInfos.Length && frameInfos[frameIndex].DisposalMethod == SKCodecAnimationDisposalMethod.Keep
                ? frameIndex
                : -1;
        }

        /// <summary>Drops the decoded frames. They are decoded again if the emote is drawn later.</summary>
        public void ReleaseFrames()
        {
            lock (_decodeLock)
            {
                var frames = Interlocked.Exchange(ref _emoteFrames, null);
                if (frames is null)
                {
                    return;
                }

                foreach (var image in frames)
                {
                    image?.Dispose();
                }
            }
        }

        /// <summary>
        /// Resizes the emote to have a <see cref="Height"/> of <paramref name="height"/>.
        /// If the nearest integer scale is within <paramref name="upSnapThreshold"/> or <paramref name="downSnapThreshold"/> of <paramref name="height"/>, it will be integer scaled instead.
        /// </summary>
        public void SnapResize(int height, int upSnapThreshold, int downSnapThreshold)
        {
            var codecInfo = Codec.Info;

            height = TwitchHelper.SnapResizeHeight(height, upSnapThreshold, downSnapThreshold, codecInfo.Height);

            SetTargetSize(new SKImageInfo((int)(height / (double)codecInfo.Height * codecInfo.Width), height));
        }

        public void Scale(double newScale) => SnapScale(newScale, 0, 0);

        public void SnapScale(double newScale, int upSnapThreshold, int downSnapThreshold)
        {
            if (Math.Abs(newScale - 1) < 0.01)
            {
                return;
            }

            var codecInfo = Codec.Info;
            var height = TwitchHelper.SnapResizeHeight((int)(codecInfo.Height * newScale), upSnapThreshold, downSnapThreshold, codecInfo.Height);

            SetTargetSize(new SKImageInfo((int)(height / (double)codecInfo.Height * codecInfo.Width), height));
        }

        // Records the size to decode at; anything already decoded is dropped so it comes back at the new size
        private void SetTargetSize(SKImageInfo info)
        {
            if (info.Width == _info.Width && info.Height == _info.Height)
            {
                return;
            }

            _info = info;
            ReleaseFrames();
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
                    ReleaseFrames();
                    Codec?.Dispose();
                }
            }
            finally
            {
                Disposed = true;
            }
        }
    }
}
