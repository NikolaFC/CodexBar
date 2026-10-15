using CodexBar.Core;

namespace CodexBar.Windows.Services;

/// <summary>
/// Wraps the core <see cref="SettingsStore"/>. In demo mode no store is created,
/// so nothing is read from or written to the user profile.
/// </summary>
public sealed class SettingsService
{
    private readonly SettingsStore? _store;
    private readonly SettingsWriter _writer = new();

    public SettingsService(bool demoMode)
    {
        if (!demoMode)
        {
            _store = new SettingsStore();
        }
    }

    /// <summary>True when changes stay in memory only.</summary>
    public bool IsEphemeral => _store is null;

    public string? FilePath => _store?.FilePath;

    public AppSettings Load()
    {
        if (_store is null)
        {
            return new AppSettings();
        }

        try
        {
            return _store.Load() ?? new AppSettings();
        }
        catch (Exception ex)
        {
            AppLog.Error("settings-load-failed", ex);
            return new AppSettings();
        }
    }

    /// <summary>
    /// Persists settings and reports the failure to the caller instead of
    /// swallowing it, so the settings window can keep itself open and show the
    /// problem rather than claiming success.
    /// </summary>
    public string? Save(AppSettings settings)
    {
        if (_store is null)
        {
            return null;
        }

        return WriteCore(settings);
    }

    /// <summary>
    /// Persists settings off the calling thread. Writes are serialized and a
    /// request that a newer one has superseded is skipped, so two quick saves
    /// cannot leave the older snapshot on disk. Demo runs never touch disk.
    /// </summary>
    public Task<string?> SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (_store is null)
        {
            return Task.FromResult<string?>(null);
        }

        return _writer.WriteAsync(() => WriteCore(settings), cancellationToken);
    }

    private string? WriteCore(AppSettings settings)
    {
        try
        {
            _store!.Save(settings);
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Error("settings-save-failed", ex);
            return $"保存失败：{ex.GetType().Name}。请检查文件路径是否可写。";
        }
    }
}

/// <summary>Read-only discovery of the default credential locations.</summary>
public static class CredentialDiscovery
{
    public static string? CodexAuthPath => ExistingFile(CodexCredentialsReader.ResolvePath(new AppSettings()));

    public static string? ClaudeCredentialsPath => ExistingFile(ClaudeCredentialsReader.ResolvePath(new AppSettings()));

    private static string? ExistingFile(string path)
    {
        try
        {
            // Same policy as the usage fingerprint: a path that is not local
            // could make File.Exists dispatch a request to another machine, so
            // reject it before any metadata probe. Environment variables such
            // as CODEX_HOME and CLAUDE_CONFIG_DIR can name a UNC share.
            if (!LocalPathProbe.IsLocal(path))
            {
                return null;
            }

            return File.Exists(path) ? path : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
