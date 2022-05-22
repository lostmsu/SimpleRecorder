// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CaptureEncoder;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI.ViewManagement;
using System.Globalization;

using static System.FormattableString;

namespace SimpleRecorder
{
    public sealed partial class MainPage : Page
    {
        public MainPage()
        {
            InitializeComponent();
            
            ApplicationView.GetForCurrentView().SetPreferredMinSize(
               new Size(350, 200));

            if (!GraphicsCaptureSession.IsSupported())
            {
                IsEnabled = false;

                var dialog = new MessageDialog(
                    "Screen capture is not supported on this device for this release of Windows!",
                    "Screen capture unsupported");

                var ignored = dialog.ShowAsync();
                return;
            }

            _device = Direct3D11Helpers.CreateDevice();

            var settings = GetCachedSettings();

            var bitrates = new List<string> {
                "1 Mbps",
                "2 Mbps",
                "4 Mbps",
                "8 Mbps",
                "16 Mbps",
                "32 Mbps",
            };
            QualityComboBox.ItemsSource = bitrates;
            Bitrate = settings.Bitrate;

            var frameRates = new List<string> { "20fps", "30fps", "60fps" };
            FrameRateComboBox.ItemsSource = frameRates;
            FrameRateComboBox.SelectedIndex = frameRates.IndexOf($"{settings.FrameRate}fps");

            UseCaptureItemSizeCheckBox.IsChecked = settings.UseSourceSize;
        }

        uint Bitrate {
            get => uint.Parse((QualityComboBox.SelectedItem as string).Replace(" Mbps", ""), CultureInfo.InvariantCulture)
                * 1000000;
            set {
                var mbps = value / 1000000;
                QualityComboBox.SelectedIndex = QualityComboBox.Items.IndexOf(Invariant($"{mbps} Mbps"));
            }
        }

        private async void ToggleButton_Checked(object sender, RoutedEventArgs e)
        {
            var button = (ToggleButton)sender;

            // Get our encoder properties
            var frameRate = uint.Parse(((string)FrameRateComboBox.SelectedItem).Replace("fps", ""), CultureInfo.InvariantCulture);
            var bitrate = this.Bitrate;
            var useSourceSize = UseCaptureItemSizeCheckBox.IsChecked.Value;

            uint width = 1920;
            uint height = 1080;

            // Get our capture item
            var picker = new GraphicsCapturePicker();
            var item = await picker.PickSingleItemAsync();
            if (item == null)
            {
                button.IsChecked = false;
                return;
            }

            // Use the capture item's size for the encoding if desired
            if (useSourceSize)
            {
                width = (uint)item.Size.Width;
                height = (uint)item.Size.Height;

                // Even if we're using the capture item's real size,
                // we still want to make sure the numbers are even.
                // Some encoders get mad if you give them odd numbers.
                width = EnsureEven(width);
                height = EnsureEven(height);
            }

            // Find a place to put our vidoe for now
            var file = await GetTempFileAsync();

            // Tell the user we've started recording
            MainTextBlock.Text = "● rec";
            var originalBrush = MainTextBlock.Foreground;
            MainTextBlock.Foreground = new SolidColorBrush(Colors.Red);
            MainProgressBar.IsIndeterminate = true;

            // Kick off the encoding
            try
            {
                using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
                using (_encoder = new Encoder(_device, item))
                {
                    await _encoder.EncodeAsync(
                        stream, 
                        width, height, bitrate, 
                        frameRate);
                }
                MainTextBlock.Foreground = originalBrush;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                Debug.WriteLine(ex);

                var message = GetMessageForHResult(ex.HResult);
                if (message == null)
                {
                    message = $"Uh-oh! Something went wrong!\n0x{ex.HResult:X8} - {ex.Message}";
                }
                var dialog = new MessageDialog(
                    message,
                    "Recording failed");

                await dialog.ShowAsync();

                button.IsChecked = false;
                MainTextBlock.Text = "failure";
                MainTextBlock.Foreground = originalBrush;
                MainProgressBar.IsIndeterminate = false;
                return;
            }

            // At this point the encoding has finished,
            // tell the user we're now saving
            MainTextBlock.Text = "saving...";

            // Ask the user where they'd like the video to live
            var newFile = await PickVideoAsync();
            if (newFile == null)
            {
                // User decided they didn't want it
                // Throw out the encoded video
                button.IsChecked = false;
                MainTextBlock.Text = "canceled";
                MainProgressBar.IsIndeterminate = false;
                await file.DeleteAsync();
                return;
            }
            // Move our vidoe to its new home
            await file.MoveAndReplaceAsync(newFile);

            // Tell the user we're done
            button.IsChecked = false;
            MainTextBlock.Text = "done";
            MainProgressBar.IsIndeterminate = false;

            // Open the final product
            await Launcher.LaunchFileAsync(newFile);
        }

        private void ToggleButton_Unchecked(object sender, RoutedEventArgs e)
        {
            // If the encoder is doing stuff, tell it to stop
            _encoder?.Dispose();
        }

        private async Task<StorageFile> PickVideoAsync()
        {
            var picker = new FileSavePicker();
            picker.SuggestedStartLocation = PickerLocationId.VideosLibrary;
            picker.SuggestedFileName = "recordedVideo";
            picker.DefaultFileExtension = ".mp4";
            picker.FileTypeChoices.Add("MP4 Video", new List<string> { ".mp4" });

            var file = await picker.PickSaveFileAsync();
            return file;
        }

        private async Task<StorageFile> GetTempFileAsync()
        {
            var folder = ApplicationData.Current.TemporaryFolder;
            var name = DateTime.Now.ToString("yyyyMMdd-HHmm-ss");
            var file = await folder.CreateFileAsync($"{name}.mp4");
            return file;
        }

        private uint EnsureEven(uint number)
        {
            if (number % 2 == 0)
            {
                return number;
            }
            else
            {
                return number + 1;
            }
        }

        private AppSettings GetCurrentSettings()
        {
            var bitrate = this.Bitrate;
            var frameRate = uint.Parse(((string)FrameRateComboBox.SelectedItem).Replace("fps", ""));
            var useSourceSize = UseCaptureItemSizeCheckBox.IsChecked.Value;

            return new AppSettings { Bitrate = bitrate, FrameRate = frameRate, UseSourceSize = useSourceSize };
        }

        private AppSettings GetCachedSettings()
        {
            var localSettings = ApplicationData.Current.LocalSettings;
            var result =  new AppSettings
            {
                Bitrate = 16 * 1024 * 1024,
                FrameRate = 20,
                UseSourceSize = true,
            };
            if (localSettings.Values.TryGetValue(nameof(AppSettings.Bitrate), out var bitrate))
            {
                result.Bitrate = (uint)bitrate;
            }
            if (localSettings.Values.TryGetValue(nameof(AppSettings.FrameRate), out var frameRate))
            {
                result.FrameRate = (uint)frameRate;
            }
            if (localSettings.Values.TryGetValue(nameof(AppSettings.UseSourceSize), out var useSourceSize))
            {
                result.UseSourceSize = (bool)useSourceSize;
            }
            return result;
        }

        public void CacheCurrentSettings()
        {
            var settings = GetCurrentSettings();
            CacheSettings(settings);
        }

        private static void CacheSettings(AppSettings settings)
        {
            var localSettings = ApplicationData.Current.LocalSettings;
            localSettings.Values[nameof(AppSettings.Bitrate)] = settings.Bitrate.ToString();
            localSettings.Values[nameof(AppSettings.FrameRate)] = settings.FrameRate;
            localSettings.Values[nameof(AppSettings.UseSourceSize)] = settings.UseSourceSize;
        }

        private static T ParseEnumValue<T>(string input)
        {
            return (T)Enum.Parse(typeof(T), input, false);
        }

        private string GetMessageForHResult(int hresult)
        {
            switch ((uint)hresult)
            {
                // MF_E_TRANSFORM_TYPE_NOT_SET
                case 0xC00D6D60:
                    return "The combination of options you've chosen are not supported by your hardware.";
                default:
                    return null;
            }
        }

        struct AppSettings
        {
            public uint Bitrate;
            public uint FrameRate;
            public bool UseSourceSize;
        }

        private IDirect3DDevice _device;
        private Encoder _encoder;
    }
}
