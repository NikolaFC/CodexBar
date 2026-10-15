namespace CodexBar.Windows;

/// <summary>Parsed command line switches for the tray app.</summary>
public sealed class AppOptions
{
    public bool IsDemo { get; init; }

    public bool Background { get; init; }

    public bool SmokeTest { get; init; }

    public string? ScreenshotPath { get; init; }

    /// <summary>
    /// True when this run should render in software. A diagnostic A/B switch for
    /// GPU-driver-specific panel lag; never persisted to settings.
    /// </summary>
    public bool UseSoftwareRendering { get; init; }

    /// <summary>True when the process should render demo screenshots and exit.</summary>
    public bool IsHeadlessRender => SmokeTest || ScreenshotPath is not null;

    public static AppOptions Parse(IReadOnlyList<string> args)
    {
        var demo = false;
        var background = false;
        var smoke = false;
        string? screenshot = null;
        var softwareRendering = false;

        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            switch (arg.ToLowerInvariant())
            {
                case "--demo":
                    demo = true;
                    break;
                case "--background":
                    background = true;
                    break;
                case "--smoke-test":
                    smoke = true;
                    break;
                case "--software-rendering":
                    softwareRendering = true;
                    break;
                case "--screenshot":
                    if (index + 1 < args.Count && !args[index + 1].StartsWith('-'))
                    {
                        screenshot = args[++index];
                    }
                    else
                    {
                        screenshot = string.Empty;
                    }

                    break;
                default:
                    if (arg.StartsWith("--screenshot=", StringComparison.OrdinalIgnoreCase))
                    {
                        screenshot = arg["--screenshot=".Length..];
                    }

                    break;
            }
        }

        return new AppOptions
        {
            IsDemo = demo,
            Background = background,
            SmokeTest = smoke,
            ScreenshotPath = screenshot,
            UseSoftwareRendering = softwareRendering,
        };
    }
}
