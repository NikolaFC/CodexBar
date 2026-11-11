using System.Text.Json;

namespace CodexBar.Core;

/// <summary>
/// Writes the latest snapshot of every provider to a small JSON file that other local programs
/// (the DSH phone adapter) read instead of fetching quota themselves.
///
/// The document is built field by field from a fixed allowlist: provider id and name, status,
/// plan, pool labels, and windows. Tokens, credential paths, emails (<see cref="UsageSnapshot.Account"/>)
/// and free-form detail text are never part of it.
/// </summary>
public static class UsageExport
{
    public const string FileName = "usage-snapshot.json";
    public const int SchemaVersion = 1;

    public static string Serialize(IEnumerable<UsageSnapshot> snapshots, DateTimeOffset generatedAt, string? app = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", SchemaVersion);
            writer.WriteString("generatedAt", generatedAt.ToUniversalTime());
            if (!string.IsNullOrWhiteSpace(app))
            {
                writer.WriteString("app", app);
            }

            writer.WriteStartArray("providers");
            foreach (var snapshot in snapshots.OrderBy(snapshot => snapshot.Provider))
            {
                WriteProvider(writer, snapshot);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Replaces <paramref name="path"/> atomically so a reader never sees half a file.</summary>
    public static void Write(string path, string json)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    public static string StatusName(ProviderStatus status) => status switch
    {
        ProviderStatus.Ready => "ready",
        ProviderStatus.NotConfigured => "notConfigured",
        ProviderStatus.AuthenticationRequired => "authenticationRequired",
        ProviderStatus.Unavailable => "unavailable",
        ProviderStatus.Disabled => "disabled",
        _ => "unknown",
    };

    public static string ProviderKey(ProviderId id) => id switch
    {
        ProviderId.Codex => "codex",
        ProviderId.Claude => "claude",
        ProviderId.Kimi => "kimi",
        ProviderId.OpenCodeGo => "opencode-go",
        _ => id.ToString().ToLowerInvariant(),
    };

    private static void WriteProvider(Utf8JsonWriter writer, UsageSnapshot snapshot)
    {
        writer.WriteStartObject();
        writer.WriteString("id", ProviderKey(snapshot.Provider));
        writer.WriteString("name", ProviderCatalog.DisplayName(snapshot.Provider));
        writer.WriteString("status", StatusName(snapshot.Status));
        WriteOptional(writer, "plan", snapshot.Plan);
        writer.WriteString("fetchedAt", snapshot.FetchedAt.ToUniversalTime());

        writer.WriteStartArray("accounts");
        foreach (var label in snapshot.PoolAccounts)
        {
            writer.WriteStringValue(label);
        }

        writer.WriteEndArray();

        writer.WriteStartArray("windows");
        foreach (var window in snapshot.Windows)
        {
            if (!double.IsFinite(window.UsedPercent))
            {
                continue;
            }

            writer.WriteStartObject();
            writer.WriteString("name", window.Name);
            writer.WriteNumber("usedPercent", Math.Round(window.UsedPercent, 2));
            if (window.ResetsAt is { } reset)
            {
                writer.WriteString("resetsAt", reset.ToUniversalTime());
            }
            else
            {
                writer.WriteNull("resetsAt");
            }

            if (window.Duration is { } duration)
            {
                writer.WriteNumber("durationSeconds", (long)duration.TotalSeconds);
            }

            WriteOptional(writer, "account", window.Account);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value.Trim());
        }
    }
}
