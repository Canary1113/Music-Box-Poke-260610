using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using MusicBox.Services;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MusicBox
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;
        private static bool _fatalDialogShown;
        public static Window? MainWindow { get; private set; }

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();
            UnhandledException += App_UnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            DebugTrace.Write("App.OnLaunched begin");
            _window = new MainWindow();
            MainWindow = _window;
            _window.Activate();
            DebugTrace.Write("App.OnLaunched activated main window");
        }

        private async void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            DebugTrace.Write($"App.UnhandledException: {e.Message}");
            e.Handled = true;
            if (_fatalDialogShown)
            {
                Application.Current?.Exit();
                return;
            }

            _fatalDialogShown = true;
            string details = e.Exception?.ToString() ?? e.Message;
            await ShowFatalErrorDialogAsync("未处理异常", details);
        }

        private void CurrentDomain_UnhandledException(object sender, System.UnhandledExceptionEventArgs e)
        {
            DebugTrace.Write($"AppDomain.UnhandledException: {e.ExceptionObject}");
        }

        private static async Task ShowFatalErrorDialogAsync(string title, string message)
        {
            bool restartNow = false;
            if (MainWindow?.Content is not FrameworkElement root || root.XamlRoot == null)
            {
                Application.Current?.Exit();
                return;
            }

            try
            {
                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = new ScrollViewer
                    {
                        MaxHeight = 420,
                        Content = new TextBlock
                        {
                            Text = message,
                            TextWrapping = TextWrapping.Wrap
                        }
                    },
                    PrimaryButtonText = "重启应用",
                    CloseButtonText = "关闭应用",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = root.XamlRoot
                };

                restartNow = await dialog.ShowAsync() == ContentDialogResult.Primary;
            }
            catch
            {
            }

            if (restartNow)
            {
                RestartApplication();
                return;
            }

            Application.Current?.Exit();
        }

        private static void RestartApplication()
        {
            try
            {
                string? executable = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(executable))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = executable,
                        UseShellExecute = true
                    });
                }
            }
            catch
            {
            }

            Application.Current?.Exit();
        }
    }
}
