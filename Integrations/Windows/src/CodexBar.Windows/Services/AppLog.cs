namespace CodexBar.Windows.Services;

/// <summary>
/// Append-only diagnostics log restricted to caller-supplied, compile-time
/// constant event names. Exception details are reduced to the exception type
/// name so credential material can never reach the log, and logging is switched
/// off entirely for demo, smoke-test, and screenshot runs.
/// </summary>
internal static class AppLog
{
    private static readonly object Gate = new();
    private static bool _disabled;

    /// <summary>Called before any logging for --demo / --smoke-test / --screenshot runs.</summary>
    public static void DisableForDemo() => _disabled = true;

    public static void Info(string eventName) => Write("INFO", eventName);

    public static void Warn(string eventName) => Write("WARN", eventName);

    /// <summary>
    /// Records a fixed event name plus the exception type only. Never pass a raw
    /// exception message, HTTP payload, or credential value through here.
    /// </summary>
    public static void Error(string eventName, Exception exception)
        => Write("ERROR", $"{eventName} ({exception.GetType().Name})");

    private static string LogDirectory
    {
        get
        {
            var overrideDirectory = Environment.GetEnvironmentVariable("CODEXBAR_DATA_DIR");
            return string.IsNullOrWhiteSpace(overrideDirectory)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CodexBar.Windows")
                : overrideDirectory!;
        }
    }

    private static void Write(string level, string eventName)
    {
        if (_disabled)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(
                    Path.Combine(LogDirectory, "codexbar.log"),
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {eventName}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // Diagnostics must never take the app down.
        }
    }
}
