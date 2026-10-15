using System.Diagnostics;
using CodexBar.Core;

namespace CodexBar.Windows.Services;

/// <summary>Opens official usage pages through the shell with a literal URL.</summary>
public static class UsageLinks
{
    public static string UsagePage(ProviderId provider) => provider == ProviderId.Codex
        ? "https://chatgpt.com/codex/settings/usage"
        : "https://claude.ai/settings/usage";

    public static string LoginInstructions(ProviderId provider) => provider == ProviderId.Codex
        ? "请在本机终端运行 `codex login`，完成后回到 CodexBar 点击刷新。\n\n" +
          "CodexBar 只读取 ~/.codex/auth.json，不会刷新或改写令牌。"
        : "请在本机终端运行 `claude login`，完成后回到 CodexBar 点击刷新。\n\n" +
          "CodexBar 只读取 ~/.claude/.credentials.json，不会刷新或改写令牌。";

    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error("open-url-failed", ex);
        }
    }
}
