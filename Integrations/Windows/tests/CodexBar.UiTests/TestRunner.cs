using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CodexBar.UiTests;

internal sealed class AssertionFailedException : Exception
{
    public AssertionFailedException(string message)
        : base(message)
    {
    }
}

internal static class Check
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new AssertionFailedException(message);
        }
    }

    public static void False(bool condition, string message) => True(!condition, message);

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new AssertionFailedException(
                $"{message} (expected: {Render(expected)}, actual: {Render(actual)})");
        }
    }

    public static void NotNull(object? value, string message)
    {
        if (value is null)
        {
            throw new AssertionFailedException($"{message} (value was null)");
        }
    }

    public static void Empty<T>(IReadOnlyCollection<T> values, string message)
    {
        if (values.Count != 0)
        {
            throw new AssertionFailedException($"{message} (count was {values.Count})");
        }
    }

    public static void Count<T>(int expected, IReadOnlyCollection<T> values, string message)
    {
        if (values.Count != expected)
        {
            throw new AssertionFailedException($"{message} (expected {expected}, actual {values.Count})");
        }
    }

    private static string Render(object? value) => value switch
    {
        null => "null",
        string text => $"\"{text}\"",
        _ => value.ToString() ?? "<unknown>",
    };
}

internal sealed record TestCase(string Name, Action Run);

internal static class TestRunner
{
    private static readonly List<TestCase> Cases = new();

    public static void Add(string name, Action test) => Cases.Add(new TestCase(name, test));

    public static int RunAll()
    {
        var failures = new List<(string Name, string Message)>();
        var passed = 0;

        foreach (var testCase in Cases)
        {
            try
            {
                testCase.Run();
                passed++;
                Console.WriteLine($"  PASS  {testCase.Name}");
            }
            catch (AssertionFailedException ex)
            {
                failures.Add((testCase.Name, ex.Message));
                Console.WriteLine($"  FAIL  {testCase.Name}");
                Console.WriteLine($"        {ex.Message}");
            }
            catch (Exception ex)
            {
                failures.Add((testCase.Name, $"{ex.GetType().Name}: {ex.Message}"));
                Console.WriteLine($"  ERROR {testCase.Name}");
                Console.WriteLine($"        {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Total: {Cases.Count}  Passed: {passed}  Failed: {failures.Count}");

        if (failures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Failures:");
            foreach (var (name, message) in failures)
            {
                Console.WriteLine($"  - {name}: {message}");
            }

            return 1;
        }

        return 0;
    }

    public static void Reset()
    {
        Cases.Clear();
        Scratch.Reset();
    }
}

/// <summary>
/// Scratch directory under the repository's gitignored work/temp folder. Never
/// points at a real credential store or the real settings file.
/// </summary>
internal static class Scratch
{
    private static string? _root;

    public static string Root => _root ??= CreateRoot();

    public static void Reset() => _root = null;

    public static string NewDirectory(string label)
    {
        var directory = Path.Combine(Root, label + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// Verifies the redirection in <c>Program</c> actually took effect. Throws
    /// before any test runs, so a misconfigured harness cannot silently read a
    /// developer's real credential files.
    /// </summary>
    public static void AssertProfileIsRedirected()
    {
        foreach (var name in new[] { "CODEXBAR_DATA_DIR", "CODEX_HOME", "CLAUDE_CONFIG_DIR" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value) ||
                !Path.GetFullPath(value).StartsWith(Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{name} must point inside the scratch workspace '{Root}', but was '{value}'.");
            }
        }
    }

    public static string WriteFile(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private static string CreateRoot()
    {
        var repositoryRoot = FindRepositoryRoot();
        var basePath = repositoryRoot is null
            ? Path.Combine(Path.GetTempPath(), "codexbar-ui-tests")
            : Path.Combine(repositoryRoot, "work", "temp", "codexbar-ui-tests");

        var root = Path.Combine(basePath, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "CodexBar.Core")) &&
                Directory.Exists(Path.Combine(directory.FullName, "tests")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
