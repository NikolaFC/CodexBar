using CodexBar.Core;

namespace CodexBar.Tests;

/// <summary>
/// Guards the boundaries that keep credential handling local: no remote path may be read or
/// dialled, and no credential file is read without a size limit.
/// </summary>
internal static class SecurityTests
{
    private const string UncPath = @"\\attacker.example\share\auth.json";
    private const string DevicePath = @"\\?\C:\Users\fake\.codex\auth.json";

    public static void Register()
    {
        TestHarness.Add("paths: rejects network and remote schemes", RejectsRemotePaths);
        TestHarness.Add("paths: rejects UNC and device paths", RejectsUncAndDevicePaths);
        TestHarness.Add("paths: accepts ordinary drive and relative paths", AcceptsLocalPaths);
        TestHarness.Add("paths: accepts a local file URI and rejects a remote one", HandlesFileUris);
        TestHarness.AddAsync("codex: a URL credential path is refused before any request", CodexRefusesRemotePath);
        TestHarness.AddAsync("claude: a URL credential path is refused before any request", ClaudeRefusesRemotePath);
        TestHarness.AddAsync("codex: an oversized credential file is refused", CodexOversizeCredentials);
        TestHarness.Add("credentials: CODEX_HOME drives the default path", CodexHomeOverride);
        TestHarness.Add("credentials: CLAUDE_CONFIG_DIR drives the default path", ClaudeConfigDirOverride);
        TestHarness.Add("credentials: an explicit path beats the environment", ExplicitPathWins);
        TestHarness.Add("credentials: a JWT account id is recovered from the token", AccountIdFromJwt);
        TestHarness.Add("credentials: an implausible JWT expiry is ignored", ImplausibleExpiryIgnored);
    }

    private static void RejectsRemotePaths()
    {
        foreach (var path in new[]
        {
            "https://attacker.example/auth.json",
            "http://attacker.example/auth.json",
            "ftp://attacker.example/auth.json",
        })
        {
            Check.False(LocalPathProbe.IsLocal(path), $"'{path}' must be rejected");
        }

        Check.False(LocalPathProbe.IsLocal(null), "null is not a path");
        Check.False(LocalPathProbe.IsLocal("   "), "whitespace is not a path");
    }

    private static void RejectsUncAndDevicePaths()
    {
        Check.False(LocalPathProbe.IsLocal(UncPath), "a UNC path can dispatch an SMB request");
        Check.False(LocalPathProbe.IsLocal(DevicePath), "a device path is not a credential file");
        Check.False(LocalPathProbe.IsLocal(@"//attacker.example/share/auth.json"), "forward-slash UNC");
        Check.False(LocalPathProbe.IsLocal(@"\\.\C:\auth.json"), "a device namespace path is rejected");
    }

    private static void AcceptsLocalPaths()
    {
        Check.True(LocalPathProbe.IsLocal(@"C:\Users\fake\.codex\auth.json"), "drive path");
        Check.True(LocalPathProbe.IsLocal("auth.json"), "relative path");
        Check.True(LocalPathProbe.IsLocal(@"sub\dir\auth.json"), "relative subdirectory path");
        Check.True(LocalPathProbe.IsLocal("~/.codex/auth.json"), "home-relative path");
    }

    private static void HandlesFileUris()
    {
        Check.True(LocalPathProbe.IsLocal("file:///C:/Users/fake/.codex/auth.json"), "local file URI");
        Check.False(LocalPathProbe.IsLocal("file://attacker.example/share/auth.json"), "remote file URI");

        var normalized = LocalPathProbe.Normalize("file:///C:/Users/fake/.codex/auth.json");
        Check.Contains(normalized, "auth.json", "a local file URI resolves to a path");
        Check.DoesNotContain(normalized, "file:", "the scheme is not part of the path");
    }

    private static async Task CodexRefusesRemotePath()
    {
        var (provider, handler, _) = ProviderTests.CodexUnderTest();

        var snapshot = await provider.FetchAsync(ProviderTests.CodexSettings("https://attacker.example/auth.json"));

        Check.Equal(ProviderStatus.Unavailable, snapshot.Status, "status");
        Check.Contains(snapshot.Message!, "本地文件路径", "explains the local-path requirement");
        Check.Equal(0, handler.SendCount, "no request is made for a remote credential path");
    }

    private static async Task ClaudeRefusesRemotePath()
    {
        var (provider, handler, _) = ProviderTests.ClaudeUnderTest();

        var snapshot = await provider.FetchAsync(
            ProviderTests.ClaudeSettings(@"\\attacker.example\share\.credentials.json"));

        Check.Equal(ProviderStatus.Unavailable, snapshot.Status, "status");
        Check.Equal(0, handler.SendCount, "no request is made for a UNC credential path");
    }

    private static async Task CodexOversizeCredentials()
    {
        var directory = TempWorkspace.NewDirectory("oversize-credentials");
        var path = Path.Combine(directory, "auth.json");
        File.WriteAllText(path, new string('x', 5 * 1024 * 1024));
        var (provider, handler, _) = ProviderTests.CodexUnderTest();

        var snapshot = await provider.FetchAsync(ProviderTests.CodexSettings(path));

        Check.Equal(ProviderStatus.Unavailable, snapshot.Status, "status");
        Check.Contains(snapshot.Message!, "异常巨大", "explains the size refusal");
        Check.Equal(0, handler.SendCount, "a file that large is never read or sent");
    }

    private static void CodexHomeOverride()
    {
        var directory = TempWorkspace.NewDirectory("codex-home");
        using var scope = new EnvironmentScope(CodexCredentialsReader.HomeEnvironmentVariable, directory);

        var path = CodexCredentialsReader.ResolvePath(new AppSettings());

        Check.Equal(
            Path.Combine(Path.GetFullPath(directory), "auth.json"),
            path,
            "CODEX_HOME selects the credential directory");
    }

    private static void ClaudeConfigDirOverride()
    {
        var directory = TempWorkspace.NewDirectory("claude-config-dir");
        using var scope = new EnvironmentScope(ClaudeCredentialsReader.ConfigDirectoryEnvironmentVariable, directory);

        var path = ClaudeCredentialsReader.ResolvePath(new AppSettings());

        Check.Equal(
            Path.Combine(Path.GetFullPath(directory), ".credentials.json"),
            path,
            "CLAUDE_CONFIG_DIR selects the credential directory");
    }

    private static void ExplicitPathWins()
    {
        var environmentDirectory = TempWorkspace.NewDirectory("explicit-loser");
        using var scope = new EnvironmentScope(CodexCredentialsReader.HomeEnvironmentVariable, environmentDirectory);

        var explicitPath = @"D:\custom\auth.json";
        var path = CodexCredentialsReader.ResolvePath(new AppSettings { CodexAuthPath = explicitPath });

        Check.Equal(explicitPath, path, "an explicit setting wins over the environment");
    }

    private static void AccountIdFromJwt()
    {
        var (outcome, credentials) = CodexCredentialsReader.Parse(
            """
            {
              "tokens": {
                "access_token": "header.eyJleHAiOjQxMDI0NDQ4MDAsImNoYXRncHRfYWNjb3VudF9pZCI6ImFjY3RfZnJvbV9qd3QifQ.signature",
                "refresh_token": "rt-fake"
              }
            }
            """);

        Check.Equal(CredentialOutcome.Ok, outcome, "outcome");
        Check.NotNull(credentials, "credentials");
        Check.Equal("acct_from_jwt", credentials!.AccountId!, "account id recovered from the JWT claim");
    }

    private static void ImplausibleExpiryIgnored()
    {
        var (outcome, credentials) = CodexCredentialsReader.Parse(
            """
            {
              "tokens": {
                "access_token": "header.eyJleHAiOjEyM30.signature",
                "refresh_token": "rt-fake"
              }
            }
            """);

        Check.Equal(CredentialOutcome.Ok, outcome, "outcome");
        Check.Null(credentials!.ExpiresAt, "an implausible exp claim is not treated as an expiry");
        Check.False(credentials.IsExpired(DateTimeOffset.UtcNow), "no false expiry");
    }
}
