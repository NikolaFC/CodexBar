using System.Text.Json;
using CodexBar.Core.Internal;

namespace CodexBar.Core;

/// <summary>One account's own fetch result inside a provider pool.</summary>
public sealed record PoolMember(string Label, UsageSnapshot Snapshot);

/// <summary>
/// The pool's headline: the member with the most headroom left on its tightest window, and how
/// many members still have any quota at all.
/// </summary>
public sealed record PoolSummary(string BestAccount, UsageWindow Binding, int Available, int Total);

/// <summary>
/// Merges several accounts of one provider into a single card. A single member passes through
/// untouched, so one-account setups look exactly as before.
/// </summary>
public static class AccountPool
{
    public static UsageSnapshot Combine(ProviderId id, IReadOnlyList<PoolMember> members)
    {
        if (members.Count == 0)
        {
            throw new ArgumentException("A pool needs at least one member.", nameof(members));
        }

        if (members.Count == 1)
        {
            return members[0].Snapshot;
        }

        var labels = UniqueLabels(members);
        var windows = new List<UsageWindow>();
        var problems = new List<string>();
        for (var index = 0; index < members.Count; index++)
        {
            var snapshot = members[index].Snapshot;
            var label = labels[index];
            if (snapshot.Status == ProviderStatus.Ready)
            {
                windows.AddRange(snapshot.Windows.Select(window => window with { Account = label }));
            }
            else
            {
                problems.Add($"{label}：{snapshot.Message ?? "暂不可用"}");
            }
        }

        var status = members.Any(member => member.Snapshot.Status == ProviderStatus.Ready)
            ? ProviderStatus.Ready
            : members.Any(member => member.Snapshot.Status == ProviderStatus.AuthenticationRequired)
                ? ProviderStatus.AuthenticationRequired
                : members.All(member => member.Snapshot.Status == ProviderStatus.NotConfigured)
                    ? ProviderStatus.NotConfigured
                    : ProviderStatus.Unavailable;

        return new UsageSnapshot
        {
            Provider = id,
            Status = status,
            Plan = $"{members.Count} 个号",
            Message = problems.Count == 0 ? null : string.Join(" ", problems),
            Windows = windows,
            Source = string.Join(" + ", members.Select(member => member.Snapshot.Source).Distinct()),
            PoolAccounts = labels,
        };
    }

    /// <summary>Null unless the snapshot carries windows from two or more pool members.</summary>
    public static PoolSummary? Summarize(UsageSnapshot snapshot)
    {
        if (snapshot.PoolAccounts.Count < 2)
        {
            return null;
        }

        var bindings = snapshot.Windows
            .Where(window => window.Account is not null)
            .GroupBy(window => window.Account!, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(window => window.UsedPercent).First())
            .ToList();
        if (bindings.Count == 0)
        {
            return null;
        }

        var best = bindings
            .OrderBy(window => window.UsedPercent)
            .ThenBy(window => window.ResetsAt ?? DateTimeOffset.MaxValue)
            .First();
        return new PoolSummary(
            best.Account!,
            best,
            bindings.Count(window => window.UsedPercent < 100),
            snapshot.PoolAccounts.Count);
    }

    private static List<string> UniqueLabels(IReadOnlyList<PoolMember> members)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var labels = new List<string>(members.Count);
        foreach (var member in members)
        {
            var label = string.IsNullOrWhiteSpace(member.Label) ? "账号" : member.Label.Trim();
            seen[label] = seen.TryGetValue(label, out var count) ? count + 1 : 1;
            labels.Add(seen[label] == 1 ? label : $"{label} #{seen[label]}");
        }

        return labels;
    }
}

/// <summary>
/// Read-only view of the Codex accounts in a Hermes <c>auth.json</c> credential pool. Only the
/// access token and label of each <c>credential_pool.openai-codex</c> entry are kept; refresh
/// tokens and every other provider in the file are never held. Hermes stays the token owner.
/// </summary>
public static class HermesCredentialPool
{
    public const string CodexPoolKey = "openai-codex";

    public static (CredentialOutcome Outcome, IReadOnlyList<(string Label, CodexCredentials Credentials)> Accounts)
        LoadCodex(string path)
    {
        var none = Array.Empty<(string, CodexCredentials)>();
        var read = LocalFile.TryReadBounded(path, out var bytes);
        switch (read)
        {
            case LocalReadResult.Ok:
                break;
            case LocalReadResult.Missing:
                return (CredentialOutcome.Missing, none);
            case LocalReadResult.NotLocalPath:
                return (CredentialOutcome.NotLocalPath, none);
            case LocalReadResult.TooLarge:
                return (CredentialOutcome.TooLarge, none);
            default:
                return (CredentialOutcome.Unreadable, none);
        }

        try
        {
            using var document = JsonDocument.Parse(LocalFile.DecodeUtf8(bytes));
            var pool = JsonUtil.ObjectOrNull(document.RootElement, "credential_pool");
            if (pool is not { } pools ||
                !pools.TryGetProperty(CodexPoolKey, out var entries) ||
                entries.ValueKind != JsonValueKind.Array)
            {
                return (CredentialOutcome.Ok, none);
            }

            var accounts = new List<(string, CodexCredentials)>();
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object ||
                    (JsonUtil.NonEmptyString(entry, "auth_type") is { } type && type != "oauth") ||
                    JsonUtil.NonEmptyString(entry, "access_token") is not { } token)
                {
                    continue;
                }

                var label = JsonUtil.FirstNonEmpty(
                    JsonUtil.NonEmptyString(entry, "label"),
                    JsonUtil.NonEmptyString(entry, "id")) ?? "Hermes";
                accounts.Add((label, new CodexCredentials(
                    token,
                    CodexCredentialsReader.AccountIdFromJwt(token),
                    CodexCredentialsReader.ExpirationFromJwt(token))));
            }

            return (CredentialOutcome.Ok, accounts);
        }
        catch (JsonException)
        {
            return (CredentialOutcome.Malformed, none);
        }
    }
}
