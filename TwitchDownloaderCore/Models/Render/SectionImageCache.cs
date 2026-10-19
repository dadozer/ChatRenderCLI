using System.Collections.Concurrent;

namespace TwitchDownloaderCore.Models.Render
{
    internal sealed class SectionImageCache : IDisposable
    {
        private readonly ConcurrentDictionary<(int Width, int Height), ConcurrentQueue<SectionImage>> _cache = [];

        public SectionImage Rent(int width, int height)
        {
            if (_cache.TryGetValue((width, height), out var queue) && queue.TryDequeue(out var image))
            {
                image.Canvas.Clear();
                return image;
            }

            return new SectionImage(width, height);
        }

        public void Return(SectionImage sectionImage)
        {
            var queue = _cache.GetOrAdd((sectionImage.Info.Width, sectionImage.Info.Height), static _ => new ConcurrentQueue<SectionImage>());
            queue.Enqueue(sectionImage);
        }

        public void Dispose()
        {
            foreach (var queue in _cache.Values)
            {
                while (queue.TryDequeue(out var sectionImage))
                {
                    sectionImage.Dispose();
                }
            }
            _cache.Clear();
        }
    }
}