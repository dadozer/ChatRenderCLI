using SkiaSharp;

namespace TwitchDownloaderCore.Models.Render
{
    public class UpdateFrame
    {
        public SKBitmap Image { get; set; }
        public SKCanvas Canvas { get; set; }
        public List<CommentSection> Comments { get; set; } = [];
        public int CommentIndex { get; set; }
        public bool HasAnimatedEmotes { get; set; }
        public long Version { get; set; }
    }
}
