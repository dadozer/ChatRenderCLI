using System.IO;
using System.Windows;
using System.Windows.Threading;
using TwitchDownloaderWPF.Properties;

namespace TwitchDownloaderWPF
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            UpgradeSettings();

            base.OnStartup(e);

            Current.DispatcherUnhandledException += Current_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            // Set the working dir to the app dir in case we inherited a different working dir
            Directory.SetCurrentDirectory(AppContext.BaseDirectory);

            MainWindow = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            MainWindow.Show();
        }

        private static void UpgradeSettings()
        {
            if (Settings.Default.UpgradeRequired)
            {
                Settings.Default.Upgrade();
                Settings.Default.UpgradeRequired = false;
                Settings.Default.Save();
            }
        }

        private static void Current_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            ShowRecoverableExceptionMessage(e.Exception);
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = (Exception)e.ExceptionObject;

            if (e.IsTerminating)
            {
                MessageBox.Show(ex.ToString(), "Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            ShowRecoverableExceptionMessage(ex);
        }

        private static void ShowRecoverableExceptionMessage(Exception exception)
        {
            var message = exception + Environment.NewLine + Environment.NewLine + Environment.NewLine +
                          "ChatRenderGUI can resume but may be unstable. Do you want to resume?";
            var result = MessageBox.Show(message, "Fatal Error", MessageBoxButton.YesNo, MessageBoxImage.Error);

            if (result is MessageBoxResult.No)
            {
                Current?.Shutdown();
            }
        }
    }
}
