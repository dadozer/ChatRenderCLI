using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using TwitchDownloaderCore.Extensions;
using TwitchDownloaderWPF.Properties;
using TwitchDownloaderWPF.Services;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;

namespace TwitchDownloaderWPF
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public static PageChatRender pageChatRender = new PageChatRender();

        public MainWindow()
        {
            InitializeComponent();
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Main.Content = pageChatRender;

            FlashTaskbarIconIfNotForeground(TimeSpan.FromSeconds(3));

            var currentVersion = Assembly.GetExecutingAssembly().GetName().Version!.StripRevisionIfDefault();
#if DEBUG
            Title = $"Chat Render v{currentVersion} - DEBUG";
#else
            Title = $"Chat Render v{currentVersion}";
#endif

            if (!File.Exists("ffmpeg.exe") || File.GetLastWriteTime("ffmpeg.exe") < DateTime.Now - TimeSpan.FromDays(365))
            {
                var oldTitle = Title;
                try
                {
                    await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official, new FfmpegDownloadProgress());
                    FlashTaskbarIconIfNotForeground(TimeSpan.FromSeconds(3));
                }
                catch (Exception ex)
                {
                    var messageBoxResult = MessageBox.Show(this,
                        $"Unable to download FFmpeg. Please manually download it from https://ffmpeg.org/download.html and place the file at {Path.Combine(Environment.CurrentDirectory, "ffmpeg.exe")}",
                        "Unable to download FFmpeg", MessageBoxButton.OKCancel, MessageBoxImage.Information);
                    if (messageBoxResult == MessageBoxResult.OK)
                    {
                        Process.Start(new ProcessStartInfo("https://ffmpeg.org/download.html") { UseShellExecute = true });
                    }

                    if (Settings.Default.VerboseErrors)
                    {
                        MessageBox.Show(this, ex.ToString(), "Verbose Error Output", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }

                Title = oldTitle;
            }
        }

        private async void FlashTaskbarIconIfNotForeground(TimeSpan flashDuration)
        {
            var currentWindow = new WindowInteropHelper(this).Handle;
            var foregroundWindow = NativeFunctions.GetForegroundWindow();
            if (currentWindow == foregroundWindow)
                return;

            var flashWInfo = new NativeFunctions.FlashWInfo
            {
                StructSize = (uint)Marshal.SizeOf<NativeFunctions.FlashWInfo>(),
                WindowHandle = currentWindow,
                Flags = NativeFunctions.FlashWInfo.FLASHW_TRAY,
                FlashCount = uint.MaxValue,
                Timeout = 0
            };
            _ = NativeFunctions.FlashWindowEx(ref flashWInfo);

            await Task.Delay(flashDuration);

            var stopFlashWInfo = new NativeFunctions.FlashWInfo
            {
                StructSize = (uint)Marshal.SizeOf<NativeFunctions.FlashWInfo>(),
                WindowHandle = currentWindow,
                Flags = NativeFunctions.FlashWInfo.FLASHW_STOP,
                FlashCount = 0,
                Timeout = 0
            };
            _ = NativeFunctions.FlashWindowEx(ref stopFlashWInfo);
        }

        private class FfmpegDownloadProgress : IProgress<ProgressInfo>
        {
            private int _lastPercent = -1;

            public void Report(ProgressInfo value)
            {
                var percent = (int)(value.DownloadedBytes / (double)value.TotalBytes * 100);

                if (percent > _lastPercent)
                {
                    var window = Application.Current.MainWindow;
                    if (window is null) return;

                    _lastPercent = percent;

                    var oldTitle = window.Title;
                    if (oldTitle.IndexOf('-') == -1) oldTitle += " -";

                    window.Title = string.Concat(
                        oldTitle.AsSpan(0, oldTitle.IndexOf('-')),
                        $"- Downloading FFmpeg {percent}%"
                    );
                }
            }
        }
    }
}
