using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexBar.Core;

/// <summary>
/// Persists <see cref="AppSettings"/> as JSON under the app data directory.
///
/// The on-disk document is an explicit DTO with a fixed field set. Access tokens, refresh tokens,
/// and cookie material are never part of it, and a future field on <see cref="AppSettings"/>
/// cannot start being written by accident.
/// </summary>
public sealed class SettingsStore
{
    public const string DataDirectoryEnvironmentVariable = "CODEXBAR_DATA_DIR";
    public const string DefaultDirectoryName = "CodexBar.Windows";
    public const string FileName = "settings.json";

    public const int MinRefreshSeconds = 60;
    public const int MaxRefreshSeconds = 3600;
    public const int DefaultRefreshSeconds = 120;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <param name="directory">
    /// Overrides the config directory. When null, <c>CODEXBAR_DATA_DIR</c> wins, then
    /// <c>%LOCALAPPDATA%\CodexBar.Windows</c>.
    /// </param>
    public SettingsStore(string? directory = null)
    {
        Directory = ResolveDirectory(directory);
        FilePath = Path.Combine(Directory, FileName);
    }

    /// <summary>Directory holding <see cref="FilePath"/>.</summary>
    public string Directory { get; }

    /// <summary>Full path of the settings document.</summary>
    public string FilePath { get; }

    public static string ResolveDirectory(string? directory)
    {
        if (!string.IsNullOrWhiteSpace(directory))
        {
            return Path.GetFullPath(directory.Trim());
        }

        var configured = Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured.Trim());
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            // Extremely unusual, but never return a relative path the app cannot find again.
            localAppData = AppContext.BaseDirectory;
        }

        return Path.Combine(localAppData, DefaultDirectoryName);
    }

    /// <summary>Clamps the refresh interval into the supported 60..3600 second range.</summary>
    public static int ClampRefreshSeconds(int seconds) =>
        Math.Clamp(seconds, MinRefreshSeconds, MaxRefreshSeconds);

    /// <summary>Normalizes a settings object so saved and in-memory values agree.</summary>
    public static AppSettings Normalize(AppSettings settings) => settings with
    {
        RefreshSeconds = ClampRefreshSeconds(settings.RefreshSeconds),
        CodexAuthPath = NullIfBlank(settings.CodexAuthPath),
        ClaudeCredentialsPath = NullIfBlank(settings.ClaudeCredentialsPath),
    };

    /// <summary>
    /// Loads settings. A missing, unreadable, or corrupt document yields defaults instead of an
    /// exception, so a bad file never stops the tray from starting.
    /// </summary>
    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return Normalize(new AppSettings());
            }

            var json = File.ReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return Normalize(new AppSettings());
            }

            var dto = JsonSerializer.Deserialize<SettingsDocument>(json, SerializerOptions);
            return dto is null ? Normalize(new AppSettings()) : Normalize(dto.ToSettings());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Normalize(new AppSettings());
        }
    }

    /// <summary>Writes settings atomically so a crash cannot leave a half-written document.</summary>
    public void Save(AppSettings settings)
    {
        var normalized = Normalize(settings);
        System.IO.Directory.CreateDirectory(Directory);

        var document = SettingsDocument.FromSettings(normalized);
        var json = JsonSerializer.Serialize(document, SerializerOptions);

        var tempPath = FilePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A leftover temp file is harmless; the next save uses a fresh name.
                }
            }
        }
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// The exact persisted shape. Deliberately separate from <see cref="AppSettings"/> so the file
    /// format is an explicit contract.
    /// </summary>
    private sealed class SettingsDocument
    {
        public int RefreshSeconds { get; set; } = DefaultRefreshSeconds;

        public bool CodexEnabled { get; set; } = true;

        public bool ClaudeEnabled { get; set; } = true;

        public string? CodexAuthPath { get; set; }

        public string? ClaudeCredentialsPath { get; set; }

        public bool StartWithWindows { get; set; }

        public bool NotifyAtLowQuota { get; set; }

        public bool ShowRemaining { get; set; } = true;

        public static SettingsDocument FromSettings(AppSettings settings) => new()
        {
            RefreshSeconds = settings.RefreshSeconds,
            CodexEnabled = settings.CodexEnabled,
            ClaudeEnabled = settings.ClaudeEnabled,
            CodexAuthPath = settings.CodexAuthPath,
            ClaudeCredentialsPath = settings.ClaudeCredentialsPath,
            StartWithWindows = settings.StartWithWindows,
            NotifyAtLowQuota = settings.NotifyAtLowQuota,
            ShowRemaining = settings.ShowRemaining,
        };

        public AppSettings ToSettings() => new()
        {
            RefreshSeconds = RefreshSeconds,
            CodexEnabled = CodexEnabled,
            ClaudeEnabled = ClaudeEnabled,
            CodexAuthPath = CodexAuthPath,
            ClaudeCredentialsPath = ClaudeCredentialsPath,
            StartWithWindows = StartWithWindows,
            NotifyAtLowQuota = NotifyAtLowQuota,
            ShowRemaining = ShowRemaining,
        };
    }
}
