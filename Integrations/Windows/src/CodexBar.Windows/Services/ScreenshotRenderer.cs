using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexBar.Core;
using CodexBar.Windows.ViewModels;
using CodexBar.Windows.Views;

namespace CodexBar.Windows.Services;

/// <summary>
/// Headless renderer for --smoke-test / --screenshot. It builds the normal views
/// from deterministic demo data and writes PNGs with RenderTargetBitmap. No
/// credentials, settings file, registry entry, network call, or log write
/// happens on this path.
/// </summary>
public static class ScreenshotRenderer
{
    private const int PanelWidth = 480;
    private const int PanelHeight = 680;
    private const int SettingsWidth = 520;
    private const int SettingsHeight = 700;

    /// <summary>Renders the panel (and a settings companion image) and returns a process exit code.</summary>
    public static int Run(AppOptions options)
    {
        var requested = options.ScreenshotPath;
        if (string.IsNullOrWhiteSpace(requested))
        {
            Console.Error.WriteLine("--smoke-test/--screenshot requires an absolute output path, e.g. --screenshot C:\\temp\\codexbar.png");
            return 2;
        }

        if (!Path.IsPathFullyQualified(requested))
        {
            Console.Error.WriteLine($"--screenshot path must be absolute: {requested}");
            return 2;
        }

        try
        {
            var directory = Path.GetDirectoryName(requested);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            RenderPanel(requested);
            RenderSettings(CompanionPath(requested));
            Console.WriteLine($"rendered: {requested}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"render failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static void RenderPanel(string path)
    {
        var viewModel = BuildDemoViewModel();
        var window = new MainWindow(viewModel)
        {
            Width = PanelWidth,
            Height = PanelHeight,
        };

        var root = window.RenderRoot;
        Render(root, PanelWidth, PanelHeight, path);
    }

    private static void RenderSettings(string path)
    {
        var settings = new AppSettings
        {
            RefreshSeconds = 120,
            CodexEnabled = true,
            ClaudeEnabled = true,
            StartWithWindows = false,
            NotifyAtLowQuota = false,
        };

        var window = new SettingsWindow(demoMode: true, load: () => settings, save: _ => Task.FromResult<string?>(null))
        {
            Width = SettingsWidth,
            Height = SettingsHeight,
        };

        Render(window.RenderRoot, SettingsWidth, SettingsHeight, path);
    }

    /// <summary>Builds the demo panel state without touching providers or config.</summary>
    private static AppViewModel BuildDemoViewModel()
    {
        var viewModel = new AppViewModel(demo: true, DisplayMode.Remaining);
        viewModel.VersionText += " demo";

        foreach (var provider in new IUsageProvider[] { new DemoCodexProvider(), new DemoClaudeProvider() })
        {
            viewModel.Apply(provider.FetchAsync(new AppSettings()).GetAwaiter().GetResult());
        }

        viewModel.Tick();
        return viewModel;
    }

    private static void Render(FrameworkElement element, int width, int height, string path)
    {
        var size = new Size(width, height);
        element.Measure(size);
        element.Arrange(new Rect(size));
        element.UpdateLayout();

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static string CompanionPath(string path)
    {
        var directory = Path.GetDirectoryName(path);
        var name = Path.GetFileNameWithoutExtension(path);
        var companion = name + ".settings.png";
        return string.IsNullOrEmpty(directory) ? companion : Path.Combine(directory, companion);
    }
}
