using System;
using CodexBar.UiTests;

// Keep the whole run away from the real profile: the log goes to a scratch
// directory, no window is ever created, and no registry value is touched.
Environment.SetEnvironmentVariable("CODEXBAR_DATA_DIR", Scratch.Root);
// A test that builds a default AppSettings would otherwise make the usage
// fingerprint inspect the real credential files. Point both CLI home
// variables at empty scratch directories so no real profile path is ever
// resolved, let alone probed.
Environment.SetEnvironmentVariable("CODEX_HOME", Scratch.NewDirectory("codex-home"));
Environment.SetEnvironmentVariable(
    "CLAUDE_CONFIG_DIR",
    Scratch.NewDirectory("claude-config"));

// Fail fast rather than probe a real profile if redirection ever stops working.
Scratch.AssertProfileIsRedirected();

TestRunner.Reset();

UsageServiceTests.Register();
LowQuotaNotifierTests.Register();
AppOptionsTests.Register();
CardActionTests.Register();

Console.WriteLine("CodexBar.UI regression runner");
Console.WriteLine($"Temp workspace: {Scratch.Root}");
Console.WriteLine();

return TestRunner.RunAll();
