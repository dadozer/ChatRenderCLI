using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using TwitchDownloaderCore.Services;
using TwitchDownloaderWPF.Models;
using TwitchDownloaderWPF.Properties;
using CheckComboBoxItem = HandyControl.Controls.CheckComboBoxItem;

namespace TwitchDownloaderWPF
{
    /// <summary>
    /// Interaction logic for WindowSettings.xaml
    /// </summary>
    public partial class WindowSettings : Window
    {
        private bool _cancelSettingsChanges = true;

        public WindowSettings()
        {
            InitializeComponent();
        }

        private void BtnTempBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog();
            if (Directory.Exists(TextTempPath.Text))
            {
                dialog.InitialDirectory = TextTempPath.Text;
            }

            if (dialog.ShowDialog(this).GetValueOrDefault())
            {
                TextTempPath.Text = dialog.FolderName;
            }
        }

        private void Window_Initialized(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(Settings.Default.TempPath))
            {
                TextTempPath.Text = Path.GetTempPath().TrimEnd('\\');
            }
            else
            {
                TextTempPath.Text = Settings.Default.TempPath;
            }

            CheckVerboseErrors.IsChecked = Settings.Default.VerboseErrors;
            CheckReduceMotion.IsChecked = Settings.Default.ReduceMotion;

            ComboLogLevels.Items.Add(new CheckComboBoxItem { Content = "Verbose", Tag = LogLevel.Verbose });
            ComboLogLevels.Items.Add(new CheckComboBoxItem { Content = "Info", Tag = LogLevel.Info });
            ComboLogLevels.Items.Add(new CheckComboBoxItem { Content = "Warning", Tag = LogLevel.Warning });
            ComboLogLevels.Items.Add(new CheckComboBoxItem { Content = "Error", Tag = LogLevel.Error });

            var currentLogLevels = (LogLevel)Settings.Default.LogLevels;
            foreach (CheckComboBoxItem item in ComboLogLevels.Items)
            {
                if (currentLogLevels.HasFlag((Enum)item.Tag))
                {
                    ComboLogLevels.SelectedItems.Add(item);
                }
            }
        }

        private void BtnClearCache_Click(object sender, RoutedEventArgs e)
        {
            var messageBoxResult = MessageBox.Show(this,
                "Are you sure you want to clear your cache?\nYou should only really do this if the program isn't working correctly",
                "Delete Confirmation", MessageBoxButton.YesNo);
            if (messageBoxResult == MessageBoxResult.Yes)
            {
                CacheDirectoryService.ClearCacheDirectory(Settings.Default.TempPath, out _);
                CacheDirectoryService.ClearCacheDirectory(Path.GetTempPath(), out _);
            }
        }

        private void Window_Closing(object sender, EventArgs e)
        {
            if (_cancelSettingsChanges)
            {
                Settings.Default.Reload();
            }
        }

        private void BtnResetSettings_OnClick(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(this,
                    "Are you sure you want to restore all settings to their default values?",
                    "Restore Settings Confirmation",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                Settings.Default.Reset();
                Settings.Default.UpgradeRequired = false;
                Settings.Default.Save();
                _cancelSettingsChanges = false;
                Close();
            }
        }

        private void BtnSaveSettings_OnClick(object sender, RoutedEventArgs e)
        {
            _cancelSettingsChanges = false;
            Settings.Default.Save();
            Close();
        }

        private void BtnCancelSettings_OnClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void TextTempPath_OnTextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsInitialized)
                return;

            Settings.Default.TempPath = TextTempPath.Text;
        }

        private void CheckVerboseErrors_OnCheckedChanged(object sender, RoutedEventArgs e)
        {
            if (!IsInitialized)
                return;

            Settings.Default.VerboseErrors = CheckVerboseErrors.IsChecked.GetValueOrDefault();
        }

        private void ComboLogLevels_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsInitialized)
                return;

            var newLogLevel = ComboLogLevels.SelectedItems
                .Cast<CheckComboBoxItem>()
                .Sum(item => (int)item.Tag);
            Settings.Default.LogLevels = newLogLevel;
        }

        private void CheckReduceMotion_OnCheckedChanged(object sender, RoutedEventArgs e)
        {
            if (!IsInitialized)
                return;

            Settings.Default.ReduceMotion = CheckReduceMotion.IsChecked.GetValueOrDefault();
        }
    }
}
