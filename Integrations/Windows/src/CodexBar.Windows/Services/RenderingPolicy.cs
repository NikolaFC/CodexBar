using System.Windows.Interop;
using System.Windows.Media;

namespace CodexBar.Windows.Services;

/// <summary>
/// Applies the process-scoped rendering mode requested by
/// <c>--software-rendering</c>, a diagnostic A/B switch for the reported
/// low-frame-rate panel drag. One unconfirmed hypothesis is WPF being held at a
/// low refresh by a variable-refresh-rate display (G-Sync / FreeSync class
/// drivers); the .NET API docs list SoftwareOnly as the remedy for low frame rate:
/// https://learn.microsoft.com/dotnet/api/system.windows.media.renderoptions.processrendermode
///
/// Process-only and not persisted. Call <see cref="Apply"/> early, before any
/// window is created, to make startup deterministic. Toggling it requires an app
/// restart because the tray is single-instance.
/// </summary>
public static class RenderingPolicy
{
    /// <summary>
    /// Forces the software renderer for this process when
    /// <c>--software-rendering</c> was passed; otherwise leaves WPF's default
    /// (hardware when available) untouched.
    /// </summary>
    public static void Apply(AppOptions options)
    {
        if (options.UseSoftwareRendering)
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        }
    }
}
