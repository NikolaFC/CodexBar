using CodexBar.Core;

namespace CodexBar.Tests;

internal static class SettingsTests
{
    public static void Register()
    {
        TestHarness.Add("settings: round-trips through the store", RoundTrip);
        TestHarness.Add("settings: clamps the refresh interval", ClampsRefresh);
        TestHarness.Add("settings: a missing file yields defaults", MissingFileDefaults);
        TestHarness.Add("settings: a corrupt file yields defaults", CorruptFileDefaults);
        TestHarness.Add("settings: the saved document contains no credential fields", NoCredentialFields);
        TestHarness.Add("settings: saving leaves no temporary files behind", NoTempLeftovers);
        TestHarness.Add("settings: the data directory can be overridden by environment", EnvironmentOverride);
        TestHarness.Add("settings: an explicit directory wins over the environment", ExplicitDirectoryWins);
        TestHarness.Add("settings: blank paths normalise to null", BlankPathsNormalise);
    }

    private static void RoundTrip()
    {
        var store = new SettingsStore(TempWorkspace.NewDirectory("settings-roundtrip"));
        var settings = new AppSettings
        {
            RefreshSeconds = 300,
            CodexEnabled = false,
            ClaudeEnabled = true,
            CodexAuthPath = @"C:\fake\codex\auth.json",
            ClaudeCredentialsPath = @"C:\fake\claude\.credentials.json",
            StartWithWindows = true,
            NotifyAtLowQuota = true,
            ShowRemaining = false,
        };

        store.Save(settings);
        var loaded = store.Load();

        Check.Equal(300, loaded.RefreshSeconds, "refresh seconds");
        Check.False(loaded.CodexEnabled, "codex enabled");
        Check.True(loaded.ClaudeEnabled, "claude enabled");
        Check.Equal(@"C:\fake\codex\auth.json", loaded.CodexAuthPath!, "codex path");
        Check.Equal(@"C:\fake\claude\.credentials.json", loaded.ClaudeCredentialsPath!, "claude path");
        Check.True(loaded.StartWithWindows, "start with windows");
        Check.True(loaded.NotifyAtLowQuota, "notify at low quota");
        Check.False(loaded.ShowRemaining, "show remaining");
    }

    private static void ClampsRefresh()
    {
        var store = new SettingsStore(TempWorkspace.NewDirectory("settings-clamp"));

        store.Save(new AppSettings { RefreshSeconds = 5 });
        Check.Equal(SettingsStore.MinRefreshSeconds, store.Load().RefreshSeconds, "low values clamp to 60");

        store.Save(new AppSettings { RefreshSeconds = 100000 });
        Check.Equal(SettingsStore.MaxRefreshSeconds, store.Load().RefreshSeconds, "high values clamp to 3600");

        Check.Equal(60, SettingsStore.ClampRefreshSeconds(0), "zero clamps up");
        Check.Equal(3600, SettingsStore.ClampRefreshSeconds(int.MaxValue), "int.MaxValue clamps down");
        Check.Equal(120, SettingsStore.ClampRefreshSeconds(120), "in-range values pass through");
    }

    private static void MissingFileDefaults()
    {
        var store = new SettingsStore(TempWorkspace.NewDirectory("settings-missing"));
        var loaded = store.Load();

        Check.Equal(SettingsStore.DefaultRefreshSeconds, loaded.RefreshSeconds, "default refresh");
        Check.True(loaded.CodexEnabled, "codex enabled by default");
        Check.True(loaded.ClaudeEnabled, "claude enabled by default");
        Check.Null(loaded.CodexAuthPath, "no codex path");
    }

    private static void CorruptFileDefaults()
    {
        var directory = TempWorkspace.NewDirectory("settings-corrupt");
        TempWorkspace.WriteFile(directory, SettingsStore.FileName, "{ not json at all");
        var store = new SettingsStore(directory);

        var loaded = store.Load();

        Check.Equal(SettingsStore.DefaultRefreshSeconds, loaded.RefreshSeconds, "defaults survive a corrupt file");
    }

    private static void NoCredentialFields()
    {
        var directory = TempWorkspace.NewDirectory("settings-credentials");
        var store = new SettingsStore(directory);
        store.Save(new AppSettings { RefreshSeconds = 180 });

        var json = File.ReadAllText(store.FilePath);

        foreach (var forbidden in new[]
        {
            "accessToken", "access_token", "refreshToken", "refresh_token", "token", "cookie",
        })
        {
            Check.DoesNotContain(json, forbidden, $"settings must not persist '{forbidden}'");
        }
    }

    private static void NoTempLeftovers()
    {
        var directory = TempWorkspace.NewDirectory("settings-temp");
        var store = new SettingsStore(directory);
        store.Save(new AppSettings { RefreshSeconds = 200 });
        store.Save(new AppSettings { RefreshSeconds = 240 });

        var files = Directory.GetFiles(directory);

        Check.Equal(1, files.Length, "only the settings document remains");
        Check.Equal(
            SettingsStore.FileName,
            Path.GetFileName(files[0]),
            "the surviving file is the settings document");
    }

    private static void EnvironmentOverride()
    {
        var directory = TempWorkspace.NewDirectory("settings-env");
        using var scope = new EnvironmentScope(SettingsStore.DataDirectoryEnvironmentVariable, directory);

        var store = new SettingsStore();

        Check.Equal(Path.GetFullPath(directory), store.Directory, "environment data directory is used");
        Check.Equal(
            Path.Combine(Path.GetFullPath(directory), SettingsStore.FileName),
            store.FilePath,
            "file path follows the directory");
    }

    private static void ExplicitDirectoryWins()
    {
        var environmentDirectory = TempWorkspace.NewDirectory("settings-env-loser");
        var explicitDirectory = TempWorkspace.NewDirectory("settings-explicit");
        using var scope = new EnvironmentScope(SettingsStore.DataDirectoryEnvironmentVariable, environmentDirectory);

        var store = new SettingsStore(explicitDirectory);

        Check.Equal(Path.GetFullPath(explicitDirectory), store.Directory, "an explicit directory wins");
    }

    private static void BlankPathsNormalise()
    {
        var normalized = SettingsStore.Normalize(new AppSettings
        {
            CodexAuthPath = "   ",
            ClaudeCredentialsPath = string.Empty,
            RefreshSeconds = 10,
        });

        Check.Null(normalized.CodexAuthPath, "blank paths become null");
        Check.Null(normalized.ClaudeCredentialsPath, "empty paths become null");
        Check.Equal(SettingsStore.MinRefreshSeconds, normalized.RefreshSeconds, "refresh is clamped");
    }
}

/// <summary>Sets an environment variable for the duration of a test and restores it afterwards.</summary>
internal sealed class EnvironmentScope : IDisposable
{
    private readonly string _name;
    private readonly string? _original;

    public EnvironmentScope(string name, string? value)
    {
        _name = name;
        _original = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(_name, _original);
}
