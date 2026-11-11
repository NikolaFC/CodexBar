using System.Text.Json;
using CodexBar.Core;

namespace CodexBar.Tests;

internal static class UsageExportTests
{
    public static void Register()
    {
        TestHarness.Add("export: writes provider windows with pool labels", WritesWindows);
        TestHarness.Add("export: never writes the account email or detail text", NoPrivateFields);
        TestHarness.Add("export: a non-ready provider keeps its status and no windows", NonReadyStatus);
        TestHarness.Add("export: writing leaves no temporary files behind", NoTempLeftovers);
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 11, 13, 0, 0, TimeSpan.Zero);

    private static UsageSnapshot Pool() => new()
    {
        Provider = ProviderId.Codex,
        Status = ProviderStatus.Ready,
        Plan = "2 个号",
        FetchedAt = Now,
        PoolAccounts = new[] { "Plus", "Business" },
        Windows = new[]
        {
            new UsageWindow { Name = "5小时", UsedPercent = 12.346, ResetsAt = Now.AddHours(4), Duration = TimeSpan.FromHours(5), Account = "Plus" },
            new UsageWindow { Name = "7天", UsedPercent = 90, ResetsAt = null, Account = "Business" },
        },
    };

    private static void WritesWindows()
    {
        using var document = JsonDocument.Parse(UsageExport.Serialize(new[] { Pool() }, Now, "test"));
        var root = document.RootElement;
        Check.Equal(1, root.GetProperty("schema").GetInt32(), "schema");
        var provider = root.GetProperty("providers")[0];
        Check.Equal("codex", provider.GetProperty("id").GetString(), "id");
        Check.Equal("ready", provider.GetProperty("status").GetString(), "status");
        Check.Equal(2, provider.GetProperty("accounts").GetArrayLength(), "pool labels");
        var first = provider.GetProperty("windows")[0];
        Check.Close(12.35, first.GetProperty("usedPercent").GetDouble(), "rounded percent");
        Check.Equal(18000L, first.GetProperty("durationSeconds").GetInt64(), "duration");
        Check.Equal("Plus", first.GetProperty("account").GetString(), "window account");
        Check.Equal(JsonValueKind.Null, provider.GetProperty("windows")[1].GetProperty("resetsAt").ValueKind, "unknown reset");
    }

    private static void NoPrivateFields()
    {
        var snapshot = new UsageSnapshot
        {
            Provider = ProviderId.Claude,
            Status = ProviderStatus.Ready,
            Plan = "pro",
            Account = "someone@example.com",
            Detail = "secret-detail",
            Source = "OAuth",
            Windows = new[] { new UsageWindow { Name = "5小时", UsedPercent = 31 } },
        };

        var json = UsageExport.Serialize(new[] { snapshot }, Now);
        Check.DoesNotContain(json, "someone@example.com", "email");
        Check.DoesNotContain(json, "secret-detail", "detail");
        Check.DoesNotContain(json, "OAuth", "source");
    }

    private static void NonReadyStatus()
    {
        var snapshot = new UsageSnapshot
        {
            Provider = ProviderId.Claude,
            Status = ProviderStatus.AuthenticationRequired,
            Message = "Claude 登录已过期",
        };

        using var document = JsonDocument.Parse(UsageExport.Serialize(new[] { snapshot }, Now));
        var provider = document.RootElement.GetProperty("providers")[0];
        Check.Equal("authenticationRequired", provider.GetProperty("status").GetString(), "status");
        Check.Equal(0, provider.GetProperty("windows").GetArrayLength(), "windows");
    }

    private static void NoTempLeftovers()
    {
        var directory = TempWorkspace.NewDirectory("export-atomic");
        var path = Path.Combine(directory, UsageExport.FileName);
        UsageExport.Write(path, UsageExport.Serialize(new[] { Pool() }, Now));
        UsageExport.Write(path, UsageExport.Serialize(Array.Empty<UsageSnapshot>(), Now));
        var files = Directory.GetFiles(directory);
        Check.Equal(1, files.Length, "only the snapshot remains");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Check.Equal(0, document.RootElement.GetProperty("providers").GetArrayLength(), "latest content");
    }
}
