namespace CodexBar.Core.Internal;

/// <summary>
/// User-facing, actionable Chinese messages. None of these embed a response body, a token, or
/// an exception message, so a snapshot can be rendered, logged, or copied without leaking
/// credential material.
/// </summary>
internal static class Messages
{
    public const string Disabled = "已在设置中禁用该提供方（CodexBar 不会拉取它的用量）。";

    public static string MissingCredentials(string product, string hint) =>
        $"未找到 {product} 凭据文件。请先{hint}，或在设置中指定凭据文件路径。";

    public static string UnreadableCredentials(string product) =>
        $"{product} 凭据文件无法读取（权限不足或文件被占用）。请检查文件访问权限后重试。";

    public static string CorruptCredentials(string product, string hint) =>
        $"{product} 凭据文件格式损坏，无法解析 JSON。请{hint}后重试。";

    public static string OversizeCredentials(string product) =>
        $"{product} 凭据文件异常巨大，已拒绝读取。请确认该路径指向真实的凭据文件。";

    public static string NotALocalPath(string product) =>
        $"{product} 凭据路径必须是本地文件路径，不接受网络 URL。请在设置中改为本地路径。";

    public static string ApiKeyOnly(string product, string hint) =>
        $"{product} 凭据中只有 API Key，没有 OAuth 令牌。CodexBar 需要 OAuth 登录：请{hint}。";

    public static string MissingAccessToken(string product, string hint) =>
        $"{product} 凭据缺少访问令牌（access token）。请{hint}后重试。";

    public static string Expired(string product, string hint) =>
        $"{product} 访问令牌已过期。CodexBar 只读取凭据、不会刷新或写回令牌，请{hint}后重试。";

    public static string Unauthorized(string product, int status, string hint) =>
        status is 401 or 403
            ? $"{product} 认证被拒绝（HTTP {status}）。请{hint}后重试。"
            : $"{product} 请求被拒绝（HTTP {status}）。请检查账号权限后重试。";

    public static string RateLimited(string product, string? retryAfter) =>
        string.IsNullOrWhiteSpace(retryAfter)
            ? $"{product} 请求过于频繁（HTTP 429）。请稍后再试。"
            : $"{product} 请求过于频繁（HTTP 429），建议在 {retryAfter} 后重试。";

    public static string ServerError(string product, int status) =>
        $"{product} 服务暂时不可用（HTTP {status}）。请稍后重试。";

    public static string Timeout(string product, int seconds) =>
        $"{product} 请求超时（{seconds} 秒）。请检查网络后重试。";

    public static string NetworkError(string product) =>
        $"{product} 网络请求失败。请检查网络连接与代理设置后重试。";

    public static string InvalidResponse(string product) =>
        $"{product} 返回的响应无法解析，可能是服务端格式变更。请稍后重试或更新 CodexBar。";

    public static string NoWindows(string product) =>
        $"{product} 未返回任何有效的额度窗口。请确认该账号已启用相应套餐。";

    public static string HintFor(string product) =>
        product.Contains("Claude", StringComparison.OrdinalIgnoreCase)
            ? "运行 `claude` 重新登录"
            : "运行 `codex login` 重新登录";
}
