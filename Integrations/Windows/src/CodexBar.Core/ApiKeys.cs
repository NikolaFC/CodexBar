using System.Security.Cryptography;
using System.Text;
using CodexBar.Core.Internal;

namespace CodexBar.Core;

public enum ApiKeyOutcome { Ok, Missing, EnvFileUnreadable, EnvFileNotLocal, EnvFileTooLarge }

/// <summary>
/// Result of resolving an API key. The key itself stays internal so <see cref="ToString"/>,
/// logging, and the UI can only ever see the variable name and where it came from.
/// </summary>
public sealed class ApiKeyLoad
{
    internal ApiKeyLoad(ApiKeyOutcome outcome, string? key, string? variable, string? source)
    {
        Outcome = outcome;
        Key = key;
        Variable = variable;
        Source = source;
        Fingerprint = key is null ? $"{outcome}|none" : $"{source}|{variable}|{KeyDigest(key)}";
    }

    public ApiKeyOutcome Outcome { get; }

    internal string? Key { get; }

    /// <summary>The variable that supplied the key, e.g. <c>KIMI_CODE_API_KEY</c>.</summary>
    public string? Variable { get; }

    /// <summary>"环境变量" or the env file path. Never key material.</summary>
    public string? Source { get; }

    /// <summary>
    /// Non-secret identity for cache matching: a keyed digest whose salt lives only in this
    /// process, so it is not reversible offline and never written anywhere.
    /// </summary>
    public string Fingerprint { get; }

    public override string ToString() => $"ApiKeyLoad({Outcome}, {Variable ?? "-"})";

    private static readonly byte[] ProcessSalt = RandomNumberGenerator.GetBytes(32);

    private static string KeyDigest(string key)
    {
        var digest = HMACSHA256.HashData(ProcessSalt, Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(digest, 0, 8);
    }
}

/// <summary>
/// Finds the API keys for a key-based provider: the process environment first (upstream
/// variable names, then common aliases and suffixed siblings), then the optional dotenv file from
/// settings. The file is read size-bounded and only the requested names are kept.
/// </summary>
public static class ApiKeyResolver
{
    public const long MaxEnvFileBytes = 1024 * 1024;

    public static IReadOnlyList<string> VariableNames(ProviderId id) => id switch
    {
        // KIMI_CODE_API_KEY is the upstream name; KIMI_API_KEY is how Kimi Code keys are often exported.
        ProviderId.Kimi => new[] { "KIMI_CODE_API_KEY", "KIMI_API_KEY" },
        ProviderId.OpenCodeGo => new[] { "OPENCODE_API_KEY", "OPENCODE_GO_API_KEY" },
        _ => Array.Empty<string>(),
    };

    /// <summary>
    /// Every distinct key for the provider, one per account in the pool. Besides the base names,
    /// suffixed siblings such as <c>KIMI_API_KEY_SEC</c> or <c>KIMI_API_KEY_2</c> count as extra
    /// accounts. The environment wins over the env file for the same name, and a value seen twice
    /// is one account. When nothing is found the single returned load explains why.
    /// </summary>
    /// <param name="environment">Lookup override for tests; null reads the process environment.</param>
    /// <param name="environmentNames">Names to scan for siblings; null scans the process environment
    /// only when <paramref name="environment"/> is null.</param>
    public static IReadOnlyList<ApiKeyLoad> ResolveAll(
        ProviderId id,
        AppSettings settings,
        Func<string, string?>? environment = null,
        IEnumerable<string>? environmentNames = null)
    {
        environmentNames ??= environment is null ? ProcessEnvironmentNames() : Array.Empty<string>();
        environment ??= Environment.GetEnvironmentVariable;
        var names = VariableNames(id);
        var found = new List<ApiKeyLoad>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var seenValues = new HashSet<string>(StringComparer.Ordinal);

        void Add(string name, string? value, string source, bool sibling)
        {
            if (Clean(value) is not { } key || (sibling && !LooksLikeKey(key)) || !seenNames.Add(name))
            {
                return;
            }

            if (seenValues.Add(key))
            {
                found.Add(new ApiKeyLoad(ApiKeyOutcome.Ok, key, name, source));
            }
        }

        foreach (var name in names)
        {
            Add(name, environment(name), "环境变量", sibling: false);
        }

        foreach (var name in environmentNames.Where(name => IsSibling(name, names)).Order(StringComparer.Ordinal))
        {
            Add(name, environment(name), "环境变量", sibling: true);
        }

        if (string.IsNullOrWhiteSpace(settings.ApiKeyEnvFile))
        {
            return found.Count > 0 ? found : new[] { new ApiKeyLoad(ApiKeyOutcome.Missing, null, null, null) };
        }

        var path = settings.ApiKeyEnvFile.Trim();
        var read = LocalFile.TryReadBounded(path, out var bytes, MaxEnvFileBytes);
        if (read != LocalReadResult.Ok)
        {
            if (found.Count > 0)
            {
                return found;
            }

            var outcome = read switch
            {
                LocalReadResult.Missing => ApiKeyOutcome.Missing,
                LocalReadResult.NotLocalPath => ApiKeyOutcome.EnvFileNotLocal,
                LocalReadResult.TooLarge => ApiKeyOutcome.EnvFileTooLarge,
                _ => ApiKeyOutcome.EnvFileUnreadable,
            };
            return new[] { new ApiKeyLoad(outcome, null, null, path) };
        }

        var values = ParseDotEnv(
            LocalFile.DecodeUtf8(bytes),
            name => names.Contains(name) || IsSibling(name, names));
        foreach (var name in names)
        {
            Add(name, values.GetValueOrDefault(name), path, sibling: false);
        }

        foreach (var name in values.Keys.Where(name => IsSibling(name, names)).Order(StringComparer.Ordinal))
        {
            Add(name, values[name], path, sibling: true);
        }

        return found.Count > 0 ? found : new[] { new ApiKeyLoad(ApiKeyOutcome.Missing, null, null, path) };
    }

    /// <summary><c>KIMI_API_KEY_SEC</c> is a sibling of <c>KIMI_API_KEY</c>.</summary>
    private static bool IsSibling(string name, IReadOnlyList<string> baseNames) =>
        baseNames.Any(baseName =>
            name.Length > baseName.Length + 1 &&
            name.StartsWith(baseName + "_", StringComparison.Ordinal));

    /// <summary>
    /// Siblings are discovered by name, so a <c>KIMI_API_KEY_BASE_URL</c>-style setting must not be
    /// sent as a bearer token: only opaque single-token values qualify.
    /// </summary>
    private static bool LooksLikeKey(string value) =>
        value.Length >= 16 &&
        !value.Contains("://", StringComparison.Ordinal) &&
        !value.Any(char.IsWhiteSpace);

    private static IEnumerable<string> ProcessEnvironmentNames()
    {
        try
        {
            return Environment.GetEnvironmentVariables().Keys.OfType<string>().ToList();
        }
        catch (System.Security.SecurityException)
        {
            return Array.Empty<string>();
        }
    }

    public static Dictionary<string, string> ParseDotEnv(string text, IReadOnlyCollection<string> wanted) =>
        ParseDotEnv(text, wanted.Contains);

    /// <summary>
    /// Minimal dotenv reader: <c>NAME=value</c>, optional <c>export </c>, optional matching quotes,
    /// <c>#</c> comments. Later assignments win, as in a shell. Only names accepted by
    /// <paramref name="wanted"/> are returned, so unrelated secrets in the same file are never held.
    /// </summary>
    public static Dictionary<string, string> ParseDotEnv(string text, Func<string, bool> wanted)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().TrimStart('\uFEFF').Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line[7..].TrimStart();
            }

            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var name = line[..equals].Trim();
            if (!wanted(name))
            {
                continue;
            }

            var value = line[(equals + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
            {
                value = value[1..^1];
            }
            else if (value.IndexOf(" #", StringComparison.Ordinal) is var comment and >= 0)
            {
                value = value[..comment].TrimEnd();
            }

            if (Clean(value) is { } cleaned)
            {
                result[name] = cleaned;
            }
            else
            {
                result.Remove(name);
            }
        }

        return result;
    }

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
