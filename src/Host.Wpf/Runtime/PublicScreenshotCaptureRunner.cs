using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using WindowsClientCenter.Host.ViewModels;
using WindowsClientCenter.Plugin.Abstractions.Contracts;
using Microsoft.Extensions.Logging;

namespace WindowsClientCenter.Host.Runtime;

internal sealed class PublicScreenshotCaptureRunner(
    MainWindow mainWindow,
    MainWindowViewModel viewModel,
    IHostStatusLogSink hostStatusLogSink,
    ILogger<PublicScreenshotCaptureRunner> logger)
{
    private static readonly TimeSpan InitializationDelay = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan NavigationDelay = TimeSpan.FromMilliseconds(1000);

    public async Task RunAsync(ScreenshotCaptureOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var outputDirectory = Path.GetFullPath(options.OutputDirectory, Environment.CurrentDirectory);
        Directory.CreateDirectory(outputDirectory);

        logger.LogInformation("Screenshot capture started for profile {ProfileName} into {OutputDirectory}.", options.ProfileName, outputDirectory);
        await mainWindow.WaitForInitializationAsync();
        logger.LogInformation("Main window initialization completed. Preparing screenshot window.");
        await PrepareWindowAsync(cancellationToken);
        await WaitForUiSettleAsync(InitializationDelay, cancellationToken);

        foreach (var target in HostStartupArgumentParser.GetCaptureTargets(options.ProfileName))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var navigationSucceeded = await mainWindow.Dispatcher.InvokeAsync(
                () => viewModel.TrySelectNavigationPath(target.MenuPath),
                DispatcherPriority.Normal,
                cancellationToken);

            if (!navigationSucceeded)
            {
                throw new InvalidOperationException($"Navigation target '{target.MenuPath}' was not found.");
            }

            hostStatusLogSink.Append($"Capturing screenshot '{target.FileName}' from '{target.MenuPath}'.");
            logger.LogInformation("Capturing screenshot {FileName} from {MenuPath}.", target.FileName, target.MenuPath);

            await WaitForUiSettleAsync(NavigationDelay, cancellationToken);

            var outputPath = Path.Combine(outputDirectory, target.FileName);
            await CaptureWindowToFileAsync(outputPath, cancellationToken);
        }
    }

    private async Task PrepareWindowAsync(CancellationToken cancellationToken)
    {
        await mainWindow.Dispatcher.InvokeAsync(() =>
        {
            mainWindow.WindowState = WindowState.Normal;
            mainWindow.Width = 1600;
            mainWindow.Height = 960;
            mainWindow.Left = 48;
            mainWindow.Top = 48;
            mainWindow.Topmost = true;
            mainWindow.Activate();
            mainWindow.Focus();
            mainWindow.UpdateLayout();
        }, DispatcherPriority.Normal, cancellationToken);
    }

    private async Task WaitForUiSettleAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        await Task.Delay(delay, cancellationToken);
        await mainWindow.Dispatcher.InvokeAsync(() =>
        {
            mainWindow.UpdateLayout();
        }, DispatcherPriority.Normal, cancellationToken);
    }

    private async Task CaptureWindowToFileAsync(string outputPath, CancellationToken cancellationToken)
    {
        var rect = await mainWindow.Dispatcher.InvokeAsync(() =>
        {
            viewModel.LogText = "[Demo capture] Runtime log hidden to avoid exposing local environment details.";
            mainWindow.UpdateLayout();

            var windowHandle = new WindowInteropHelper(mainWindow).Handle;
            if (windowHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("Main window handle is not available.");
            }

            if (!GetWindowRect(windowHandle, out var bounds))
            {
                throw new InvalidOperationException("Failed to resolve window bounds for screenshot capture.");
            }

            return bounds;
        }, DispatcherPriority.Normal, cancellationToken);

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("Main window bounds are empty.");
        }

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new System.Drawing.Size(width, height), CopyPixelOperation.SourceCopy);
        }

        bitmap.Save(outputPath, ImageFormat.Png);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

internal sealed class NullHostUserSettingsStore : IHostUserSettingsStore
{
    public Task<HostUserSettings> LoadAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(HostUserSettings.Empty);
    }

    public Task SaveAsync(HostUserSettings settings, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
