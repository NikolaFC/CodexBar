using Microsoft.Win32;

namespace CodexBar.Windows.Services;

/// <summary>
/// Registers the app under HKCU\...\Run only when the user opts in. The command
/// is always quoted and passed --background so a login start stays in the tray.
/// </summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CodexBar.Windows";

    /// <summary>
    /// Applies the opt-in login item. Returns null on success, or a message the
    /// settings window can show, so a registry failure is never reported as a
    /// successful save.
    /// </summary>
    public static string? Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                AppLog.Warn("startup-key-unavailable");
                return "无法写入开机启动项：注册表 Run 键不可用。";
            }

            if (enabled)
            {
                var executable = Environment.ProcessPath;
                if (string.IsNullOrEmpty(executable))
                {
                    AppLog.Warn("startup-exe-unknown");
                    return "无法注册开机启动：未取到可执行文件路径。";
                }

                key.SetValue(ValueName, $"\"{executable}\" --background", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return null;
        }
        catch (Exception ex)
        {
            AppLog.Error("startup-registration-failed", ex);
            return "无法更新开机启动项，请检查注册表权限。";
        }
    }

    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception ex)
        {
            AppLog.Error("startup-read-failed", ex);
            return false;
        }
    }
}
