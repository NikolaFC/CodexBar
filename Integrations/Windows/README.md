# CodexBar.Windows

Windows 上的托盘小工具，用来把 Codex 和 Claude 的订阅额度窗口（session / weekly 等）
直接显示在通知区域，并给出重置倒计时。它是 [CodexBar](https://github.com/NikolaFC/CodexBar)
仓库里的原生 C# / WPF 集成，位于 `Integrations/Windows`，与 macOS 菜单栏应用（Swift，
`Sources/`）和 Linux 桌面集成（Qt，`Integrations/Linux`）并列，不参与后两者的构建。

仓库分三层：`src/CodexBar.Core` 负责取数与设置（不依赖 UI），`src/CodexBar.Windows` 是
WPF + WinForms 托盘外壳，`tests/CodexBar.Tests` 是自带的自检程序。两个层之间共享的类型
定义在 [`src/CodexBar.Core/Contracts.cs`](src/CodexBar.Core/Contracts.cs)。

## 与上游的关系

| 项目 | 说明 |
| --- | --- |
| 所在仓库 | https://github.com/NikolaFC/CodexBar，本集成位于 `Integrations/Windows` |
| 移植基准 | commit `278e9056e830000532e4c3a6342dc0b2c75f00d6`，版本 `0.75.1` |
| 本项目性质 | 同仓内独立的原生 C# / WPF 实现；直接移植上游使用的 OAuth endpoint 协议与数据映射思路（上游为 Swift，本集成为 C#/.NET） |
| CI | [`.github/workflows/windows-native.yml`](../../.github/workflows/windows-native.yml)：仅在 `Integrations/Windows/**` 变更时于 `windows-latest` 上构建、自检并上传便携包 artifact |
| 许可 | MIT，沿用上游版权声明，全文见 [LICENSE](LICENSE) |
| 第三方说明 | 见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) |

上游支持大量 provider，本集成首版只覆盖 **Codex** 与 **Claude**，属于有意收窄的范围，
并不追求与上游的 provider 数量对齐。

## 运行环境

- Windows 10 / Windows 11，x64。
- 便携包（`CodexBar-win-x64-<version>.zip`）是 self-contained 单文件发布，**自带 .NET
  runtime**，目标机器不需要另外安装 .NET，也不需要管理员权限。
- 不提供安装器；解压后直接运行 `CodexBar.Windows.exe` 即可。

## 支持的 provider

| Provider | 数据来源（官方主机） | 凭据位置 |
| --- | --- | --- |
| Codex | `https://chatgpt.com/backend-api/wham/usage` | `%CODEX_HOME%`，未设置时为 `%USERPROFILE%\.codex`，读取其中的 `auth.json` |
| Claude | `https://api.anthropic.com/api/oauth/usage` | `%CLAUDE_CONFIG_DIR%`，未设置时为 `%USERPROFILE%\.claude`，读取其中的 `.credentials.json` |

两个路径都可以在设置里覆盖（`CodexAuthPath`、`ClaudeCredentialsPath`），用于凭据放在
非默认位置的情况。

工具只**读取**这些文件，不复制、不移动、不改写它们。

两个 provider 都直接走各自的 OAuth usage endpoint，请求头与响应字段的映射沿用上游
CodexBar 的做法：Codex 的额度窗口来自 `chatgpt.com` 的 usage 响应，Claude 的额度窗口来自
`api.anthropic.com` 的 OAuth usage 响应。程序自身的取数请求只发往这两个官方主机；“打开
用量页面”是把 provider 官方页面交给默认浏览器，不发程序自己的请求。

## 凭据与登录状态

- 复用 provider 官方 CLI 已经登录好的凭据（`codex` / `claude` 的登录结果）。本工具没有
  自己的登录流程，也不需要你把 token 交给它。
- 凭据过期时**不会**自动刷新 OAuth，也不会在后台重新登录：需要回到原来的 CLI 重新登录
  一次，然后本工具下次刷新即可读到新凭据。
- 纯 API key 计费不适用本工具：这里展示的是订阅额度窗口，用 `OPENAI_API_KEY` /
  `ANTHROPIC_API_KEY` 走按量计费的用量不在展示范围内。
- 不自动导入 Windows 凭据管理器 / keyring 中的条目，也不会自动进入 WSL 读取其中的凭据；
  如果你的登录状态只在 WSL 里，需要在 Windows 侧重新登录一次。

## 设置与数据位置

- 设置以 JSON 保存在 `%LOCALAPPDATA%\CodexBar.Windows`。
- 设置 `CODEXBAR_DATA_DIR` 环境变量可以整体改到这个目录之外的位置。
- 程序不把凭据写进设置文件，设置文件里只保存路径引用和界面偏好。
- 同一个目录里还有一份诊断日志 `codexbar.log`：只记录固定的事件名和异常类型名，不记录
  HTTP 响应内容、token 或凭据；demo / smoke / 截图运行完全不写日志。

可配置项（对应 [`AppSettings`](src/CodexBar.Core/Contracts.cs)）：

| 设置 | 用途 |
| --- | --- |
| `RefreshSeconds` | 自动刷新间隔，默认 120 秒 |
| `CodexEnabled` / `ClaudeEnabled` | 分别开关 Codex、Claude 的取数与显示 |
| `CodexAuthPath` / `ClaudeCredentialsPath` | 覆盖凭据文件路径 |
| `StartWithWindows` | 可选开机自启（写入当前用户的 `HKCU` Run 项，不申请管理员权限） |
| `NotifyAtLowQuota` | 额度偏低时给出提示 |
| `ShowRemaining` | 面板里的用量条与托盘提示文字显示剩余量而不是已用量（托盘图标本身固定是 Codex / Claude 两条状态条，不随数值变化） |

## 界面与行为

- 托盘图标常驻通知区域，左键切换面板，右键菜单提供“打开面板 / 立即刷新 / 设置… / 退出”。
- 关闭**主面板**只是把它隐藏到托盘，程序继续在后台运行；要真正结束请用托盘菜单里的“退出”。
- 关闭**设置窗口**只是关闭这个设置窗口，不影响主面板或托盘图标的状态。
- 可选开机自启只写当前用户的 `HKCU` 注册表项，关掉开关时会移除该项。
- 托盘提示文字（tooltip）汇总两个 provider 的百分比；面板里按 provider 显示各自的额度
  窗口和重置时间。
- 命令行开关：`--demo`（演示数据）、`--background`（启动后不显示面板）、`--smoke-test`、
  `--screenshot <绝对路径>`。程序没有 `--help`。

## demo 与 smoke 模式

为了在没有真实凭据、或者不想连网的情况下也能查看界面与自检，程序提供两种非生产模式：

- **demo**：用内置样例数据渲染界面，不读取凭据、不访问网络。
- **smoke**：走一遍启动、取数、渲染、退出的自检路径，用于构建后的快速健康检查。

```powershell
# 演示模式：用内置样例数据打开面板
.\CodexBar.Windows.exe --demo

# 自检截图：渲染主面板和设置窗口后退出（路径必须是绝对路径）
.\CodexBar.Windows.exe --smoke-test --screenshot C:\temp\codexbar.png
```

第二条命令会写出两张 PNG：`C:\temp\codexbar.png`（主面板）和 `C:\temp\codexbar.settings.png`
（设置窗口），然后进程退出。它不读凭据、不写设置、不碰注册表、不访问网络，也不会写日志。
只给 `--smoke-test` 而不给 `--screenshot` 会因为缺少绝对输出路径而退出并返回错误码 2。

## 构建与打包

在 `Integrations/Windows` 目录（本文件的同级目录）执行，使用 .NET 10 SDK
（`global.json` 固定 `10.0.402`，`rollForward: latestFeature`；CI 用
`.github/workflows/windows-native.yml` 里的 `actions/setup-dotnet` 按同一份 `global.json` 安装）：

```powershell
cd Integrations/Windows

# 构建 + 运行测试 + 生成便携包和源码包
pwsh -NoProfile -File .\build.ps1

# 本机没有全局 dotnet SDK 时指定路径
pwsh -NoProfile -File .\build.ps1 -DotNetPath C:\path\to\dotnet.exe

# 只构建并跑测试，不发布
pwsh -NoProfile -File .\build.ps1 -NoPublish

# 只构建，跳过测试和打包
pwsh -NoProfile -File .\build.ps1 -NoTests -NoPublish

# 先清理再构建
pwsh -NoProfile -File .\build.ps1 -Clean
```

产物：

| 路径 | 内容 |
| --- | --- |
| `artifacts\win-x64\` | 便携输出目录：`CodexBar.Windows.exe`（自包含单文件）、`README.md`、`LICENSE`、`THIRD_PARTY_NOTICES.md`、`licenses\dotnet-*` |
| `artifacts\CodexBar-win-x64-<version>.zip` | 便携包，解压即用，已去掉 PDB |
| `artifacts\CodexBar-source-<version>.zip` | 源码包，只含白名单内的源码、测试、文档、构建脚本和解决方案文件 |

测试由 `build.ps1` 自动运行：`tests/` 下是自带的自检程序，不依赖任何第三方测试框架。

构建脚本把 `Integrations/Windows` 当作自己的根目录：`artifacts`、`bin`、`obj` 的校验、
清理和打包都限制在这个目录之内，不读写仓库中 macOS（`Sources/`、`Tests/`）与 Linux
（`Integrations/Linux`）的内容，因此可以在 monorepo 里独立构建。

### 打包安全规则

`build.ps1` 会主动删除输出目录和 `bin`/`obj`，因此这些规则写进了脚本本身：

- 删除前一律验证目标位于**仓库之内**的严格子孙路径，按目录分隔符判定边界，
  `C:\repo-other` 不会被当成 `C:\repo` 的内容。
- 拒绝把仓库根目录、`src`、`tests`、`.git`，以及任何包含它们的祖先目录当作输出目录，
  所以 `-OutputDirectory .`、`-OutputDirectory src` 之类会直接报错而不是删代码。
- 非空的自定义输出目录必须带有上一次构建留下的标记文件（`.codexbar-output.json`）才会被
  清理，否则报错让你手动处理。
- 自定义输出目录默认不允许落在仓库之外，需要显式加 `-AllowOutsideRepositoryOutput`。
- `bin`/`obj` 清理前同样会校验目标确实位于对应项目目录之内、且在仓库之内。
- 便携包与源码包写完后会再检查一遍条目清单，发现 `bin`/`obj`/`.git`/凭据/临时文件等禁入项
  就报错，而不是产出一个可疑的压缩包。

源码包采用白名单：只收 `src/`、`tests/`、`docs/`、`.github/`、`README.md`、`LICENSE`、
`THIRD_PARTY_NOTICES.md`、`build.ps1`、`global.json`、`.gitignore`、`Directory.*.props/targets`
与解决方案文件；`bin`、`obj`、`artifacts`、SDK 目录、缓存和凭据文件一律排除。

## 安全与隐私

- 只访问 provider 的官方 API 主机，不访问第三方统计或分析服务。
- 没有遥测、没有崩溃上报、没有使用情况上传。
- 不把 token 或凭据内容写进日志。
- 凭据文件只读打开：不复制、不修改、不上传，也不写入设置文件。
- 便携包和源码包都不包含 SDK、`bin`/`obj`、缓存、凭据或临时文件。

## 已知限制

- 只支持 Codex 和 Claude 两个 provider，未实现上游的其它 provider。
- 不支持多账号：只能读取当前 CLI 已登录的那一个账号。
- 不自动刷新 OAuth，也不在工具内重新登录；凭据过期需要回到原 CLI 处理。
- 不从 Windows 凭据管理器 / keyring 导入凭据，也不自动读取 WSL 内的凭据。
- 没有后台服务、没有浏览器扩展，不需要管理员权限。

## 验证状态

**已跑通**

- 79 项自动检查全部通过：Core 56 项，界面逻辑回归 23 项。
- Debug 与 Release 两种配置都能编译通过。
- demo 与 smoke 渲染路径可跑通：用内置样例数据渲染出主面板与设置窗口截图后退出。
- Windows x64 自包含便携包已生成；打包后的 EXE 在隔离配置下通过 smoke，退出码 0，
  生成两张截图，没有写入设置或日志。测试时 `DOTNET_ROOT` 指向不存在的目录。
- `build.ps1` 的归档与打包逻辑已在 Windows PowerShell 5.1 和 PowerShell 7 下验证：
  便携包与源码包的创建、条目清单校验、白名单过滤都能正常工作。

**尚未验证**

- 尚未在另一台干净 Windows 机器上验收。
- 真实账号下 Codex / Claude 的取数结果与额度数值没有测试。
- 桌面 UI 交互（托盘点击、关闭主面板隐藏到托盘、设置窗口、退出、开机自启注册表）还没
  验收：桌面 ComputerUse 授权超时，需要后续再安排一次实际交互验收。

源码目录的 `docs/preview.png` 与 `docs/preview.settings.png` 是打包版生成的演示截图。

如果源码所在目录很深，NuGet 的运行时包缓存路径可能超过 Windows 路径长度限制。
构建时可将 `NUGET_PACKAGES` 指向一个较短、可写的缓存目录，然后重新运行 `build.ps1`。

## 许可

MIT，Copyright (c) 2026 Peter Steinberger。本项目是独立移植，保留上游版权声明，完整许可
全文见 [LICENSE](LICENSE)；第三方组件与 .NET runtime 许可说明见
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
