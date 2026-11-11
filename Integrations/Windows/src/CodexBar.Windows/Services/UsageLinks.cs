using System.Diagnostics;
using CodexBar.Core;

namespace CodexBar.Windows.Services;

/// <summary>Opens official usage pages through the shell with a literal URL.</summary>
public static class UsageLinks
{
    public static string UsagePage(ProviderId provider) => provider switch
    {
        ProviderId.Codex => "https://chatgpt.com/codex/settings/usage",
        ProviderId.Kimi => "https://www.kimi.com/code/console",
        ProviderId.OpenCodeGo => "https://opencode.ai/auth",
        _ => "https://claude.ai/settings/usage",
    };

    public static string LoginInstructions(ProviderId provider) => provider switch
    {
        ProviderId.Codex =>
            "请在本机终端运行 `codex login`，完成后回到 CodexBar 点击刷新。\n\n" +
            "CodexBar 只读取 ~/.codex/auth.json，不会刷新或改写令牌。",
        ProviderId.Kimi =>
            "在 Kimi Code 控制台（kimi.com/code/console）创建 API Key（sk-kimi-…），然后二选一：\n" +
            "• 设置用户环境变量 KIMI_CODE_API_KEY（或 KIMI_API_KEY），重启 CodexBar；\n" +
            "• 在设置里指定一个包含该变量的 .env 文件。\n\n" +
            "CodexBar 只读取 Key 用来查询额度，不会保存或改写它。",
        ProviderId.OpenCodeGo =>
            "在 opencode.ai 控制台为 OpenCode Go 工作区创建 API Key，然后二选一：\n" +
            "• 设置用户环境变量 OPENCODE_API_KEY（或 OPENCODE_GO_API_KEY），重启 CodexBar；\n" +
            "• 在设置里指定一个包含该变量的 .env 文件。\n\n" +
            "CodexBar 只读取 Key 用来查询额度，不会保存或改写它。",
        _ =>
            "请在本机终端运行 `claude login`，完成后回到 CodexBar 点击刷新。\n\n" +
            "CodexBar 只读取 ~/.claude/.credentials.json，不会刷新或改写令牌。",
    };

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
