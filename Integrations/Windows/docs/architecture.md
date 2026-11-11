# 架构说明

本文描述 CodexBar.Windows 的分层、接口与关键取舍。它对应的是当前实现阶段的设计，
凡是还没有在真实账号与真实桌面上跑通的部分都会明确标注为“尚未验证”。

## 目标与非目标

目标：在 Windows 通知区域常驻一个轻量进程，复用 provider 官方 CLI 已经登录的凭据，
定期查询订阅额度窗口，并把用量与重置倒计时显示在托盘与面板上。

首版的非目标：与上游的 provider 数量对齐、多账号、自动 OAuth 刷新、从 keyring 或 WSL
导入凭据、后台服务、安装器、遥测。

## 分层

| 层 | 位置 | 职责 |
| --- | --- | --- |
| Core | `src/CodexBar.Core` | 纯逻辑：契约、provider 取数、设置读写、刷新调度。不引用 WPF/WinForms，便于单独测试。 |
| Windows | `src/CodexBar.Windows` | WPF + WinForms 外壳：`NotifyIcon` 托盘、设置窗口、气泡提示、开机自启注册表写入。 |
| Tests | `tests/CodexBar.Tests` | 控制台自检程序，直接引用 Core（以及必要的 Windows 侧代码），无第三方测试框架。 |

Core 不感知 UI：它对外暴露的是 [`Contracts.cs`](../src/CodexBar.Core/Contracts.cs) 里的
契约（`IUsageProvider`、`UsageSnapshot`、`AppSettings`）以及取数、凭据解析、设置读写这些
服务类型；Windows 侧只消费这些类型，不反过来被 Core 引用。这样 Core 可以在没有桌面会话
的环境里单独编译和测试。

## 核心契约

契约定义在 [`src/CodexBar.Core/Contracts.cs`](../src/CodexBar.Core/Contracts.cs)：

```csharp
public enum ProviderId { Codex, Claude }

public interface IUsageProvider
{
    ProviderId Id { get; }
    Task<UsageSnapshot> FetchAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
```

`UsageSnapshot` 是一次取数的完整结果：provider、状态、套餐名、账号、窗口列表、取数时间
与数据来源。`UsageWindow` 描述单个额度窗口（名称、已用百分比、重置时间、窗口长度），
并派生 `RemainingPercent`。`AppSettings` 是设置文件的映射，同时承载两个可覆盖的凭据路径。

新增 provider 的方式就是实现 `IUsageProvider` 并注册，UI 不需要改动：它只按
`ProviderStatus` 决定显示“正常 / 未配置 / 需要登录 / 不可用 / 已禁用”。

## 数据流

1. 刷新调度按 `AppSettings.RefreshSeconds`（默认 120 秒）触发，另有 30 秒的计时器用于
   刷新倒计时文字。
2. 对每个已启用的 provider 调用 `FetchAsync`，传入当前设置，取消令牌跟随程序退出。
3. provider 定位凭据文件（设置里的覆盖路径优先，否则用环境变量默认路径），只读解析。
4. 向 provider 的官方 API 主机发起查询（Codex：`chatgpt.com` 的 usage endpoint；
   Claude：`api.anthropic.com` 的 OAuth usage endpoint），解析成 `UsageSnapshot`。
5. UI 线程按 snapshot 更新托盘图标与面板；失败时保留上一次结果并标记状态。

取数在线程池上完成，所有视图模型更新都会 marshalling 回 UI 线程。

## 凭据解析

| Provider | 默认位置 | 覆盖设置 |
| --- | --- | --- |
| Codex | `%CODEX_HOME%`，未设置时为 `%USERPROFILE%\.codex` | `CodexAuthPath` |
| Claude | `%CLAUDE_CONFIG_DIR%`，未设置时为 `%USERPROFILE%\.claude` | `ClaudeCredentialsPath` |

凭据文件一律只读打开，不复制、不移动到程序目录、不写入日志、不写入设置文件。
凭据过期时不做自动 OAuth 刷新：状态置为需要重新登录，由用户回到原 CLI 处理。

## 设置与数据目录

- 默认目录：`%LOCALAPPDATA%\CodexBar.Windows`。
- 覆盖方式：设置 `CODEXBAR_DATA_DIR` 环境变量。
- 内容：`settings.json`（`AppSettings` 的映射）和诊断日志 `codexbar.log`。
- 诊断日志只写固定事件名与异常类型名，不写 HTTP 响应内容、token 或凭据；demo / smoke /
  截图运行不写日志。
- 不存放任何凭据内容或 token。

## 打包与发布

`build.ps1` 是唯一的构建入口：restore → build → 运行测试 → `dotnet publish` 自包含单文件
win-x64 → 拷贝 `README.md`/`LICENSE`/`THIRD_PARTY_NOTICES.md` 与运行时的
`licenses/dotnet-LICENSE.txt`、`licenses/dotnet-ThirdPartyNotices.txt` → 去掉 PDB →
写便携 zip 与白名单源码 zip。CI 见 `.github/workflows/windows.yml`。

删除与清理的安全规则（严格子孙路径、拒绝仓库根/`src`/`tests`、非空目录需要标记文件、
仓库外需要显式批准）写在脚本内部，详见 [README](../README.md#打包安全规则)。

## 验证状态

本文描述的是当前实现的设计。真实环境下的行为（真实账号取数、托盘交互、开机自启、
demo / smoke 输出、长时间运行稳定性）还没有确认，验证结论由 [README](../README.md) 的
“验证状态”一节统一记录。
