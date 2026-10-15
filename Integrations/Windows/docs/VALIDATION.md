# 0.1.0 本机验收记录

日期：2026-10-15。目标：Windows x64；SDK：.NET 10.0.402。

- Release 解决方案编译：0 warnings / 0 errors。
- Core 自动检查：56/56；全部使用模拟 HTTP 与临时凭据文件。
- 界面逻辑检查：23/23；覆盖失败缓存、账号隔离、并发与取消、提醒阈值、命令参数和错误状态按钮。
- 原生 WPF 主面板和设置页：使用 RenderTargetBitmap 检查实际视图布局。
- 自包含单文件 EXE：以 `--smoke-test --screenshot <absolute path>` 运行成功，退出码 0。
- 发布版 smoke 使用隔离的 CODEXBAR_DATA_DIR、CODEX_HOME、CLAUDE_CONFIG_DIR，DOTNET_ROOT 指向不存在的目录；没有生成配置或日志。
- 便携包包含 EXE、README、MIT LICENSE、第三方声明和 .NET 实际许可文件。
- 源码包排除 SDK、bin、obj、构建缓存、测试临时数据和凭据文件。
- 构建脚本归档逻辑在 Windows PowerShell 5.1 / PowerShell 7 下验证。

未验证：真实账号在线额度、另一台干净 Windows 机器、托盘点击和设置交互、开机启动注册表。桌面自动化捕获因应用授权等待超时未完成；没有将渲染测试当作点击交互验收。

截图使用明确标注的演示数据，不代表用户实际额度。
