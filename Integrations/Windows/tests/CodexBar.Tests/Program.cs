using CodexBar.Tests;

TestHarness.Reset();

ParserTests.Register();
ProviderTests.Register();
ClaudeProviderTests.Register();
SettingsTests.Register();
SecurityTests.Register();

Console.WriteLine("CodexBar.Core test runner");
Console.WriteLine($"Temp workspace: {TempWorkspace.Root}");
Console.WriteLine();

return await TestHarness.RunAllAsync();
