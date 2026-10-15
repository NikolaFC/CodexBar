using System;
using System.IO;
using CodexBar.Windows;
using CodexBar.Windows.Services;

namespace CodexBar.UiTests;

/// <summary>
/// Command-line contract for the demo and headless-render paths. The renderer
/// must refuse anything it cannot resolve to a real file, and refuse it before
/// any window or WPF visual is created.
/// </summary>
internal static class AppOptionsTests
{
    public static void Register()
    {
        TestRunner.Add("--demo is parsed and is not headless", DemoIsParsed);
        TestRunner.Add("--background is parsed without demo", BackgroundIsParsed);
        TestRunner.Add("--smoke-test with an absolute screenshot path is headless", SmokeTestIsHeadless);
        TestRunner.Add("--screenshot=<path> form is accepted", ScreenshotEqualsForm);
        TestRunner.Add("a relative screenshot path is rejected with exit code 2", RelativePathRejected);
        TestRunner.Add("a missing screenshot path is rejected with exit code 2", MissingPathRejected);
    }

    private static void DemoIsParsed()
    {
        var options = AppOptions.Parse(new[] { "--demo" });
        Check.True(options.IsDemo, "--demo should set IsDemo");
        Check.False(options.Background, "--demo alone should not set Background");
        Check.False(options.IsHeadlessRender, "plain --demo should still open the panel");
    }

    private static void BackgroundIsParsed()
    {
        var options = AppOptions.Parse(new[] { "--background" });
        Check.True(options.Background, "--background should set Background");
        Check.False(options.IsDemo, "--background is not demo mode");
        Check.False(options.IsHeadlessRender, "--background is not a headless render");
    }

    private static void SmokeTestIsHeadless()
    {
        var target = Path.Combine(Scratch.NewDirectory("options-smoke"), "panel.png");
        var options = AppOptions.Parse(new[] { "--demo", "--smoke-test", "--screenshot", target });

        Check.True(options.IsDemo, "demo should still be tracked in a smoke run");
        Check.True(options.SmokeTest, "--smoke-test should be recorded");
        Check.True(options.IsHeadlessRender, "a smoke run must not open a panel");
        Check.Equal(target, options.ScreenshotPath, "the absolute screenshot path should be preserved");
    }

    private static void ScreenshotEqualsForm()
    {
        var target = Path.Combine(Scratch.NewDirectory("options-equals"), "panel.png");
        var options = AppOptions.Parse(new[] { $"--screenshot={target}" });

        Check.Equal(target, options.ScreenshotPath, "--screenshot=<path> should be parsed");
        Check.True(options.IsHeadlessRender, "a bare --screenshot run is a headless render");
    }

    private static void RelativePathRejected()
    {
        var options = AppOptions.Parse(new[] { "--demo", "--smoke-test", "--screenshot", "relative.png" });
        Check.Equal("relative.png", options.ScreenshotPath, "the relative path should be preserved verbatim");

        var exitCode = ScreenshotRenderer.Run(options);
        Check.Equal(2, exitCode, "a relative screenshot path should fail with exit code 2");
    }

    private static void MissingPathRejected()
    {
        var options = AppOptions.Parse(new[] { "--smoke-test" });
        Check.True(
            options.IsHeadlessRender,
            "--smoke-test must take the headless path even when no screenshot was requested");

        // Route through the renderer the way the app does: it must reject the
        // missing path rather than silently succeeding with exit code 0.
        var exitCode = ScreenshotRenderer.Run(options);
        Check.Equal(2, exitCode, "a smoke test without a screenshot path should fail with exit code 2");
    }
}
