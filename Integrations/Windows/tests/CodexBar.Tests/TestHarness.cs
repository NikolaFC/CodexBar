using System.Net;
using System.Text;

namespace CodexBar.Tests;

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
            throw new AssertionFailedException($"{message} (expected: {Render(expected)}, actual: {Render(actual)})");
        }
    }

    public static void Close(double expected, double actual, string message, double tolerance = 0.0001)
    {
        if (Math.Abs(expected - actual) > tolerance)
        {
            throw new AssertionFailedException($"{message} (expected: {expected}, actual: {actual})");
        }
    }

    public static void NotNull(object? value, string message)
    {
        if (value is null)
        {
            throw new AssertionFailedException($"{message} (value was null)");
        }
    }

    public static void Null(object? value, string message)
    {
        if (value is not null)
        {
            throw new AssertionFailedException($"{message} (value was {Render(value)})");
        }
    }

    public static void Contains(string haystack, string needle, string message)
    {
        if (!haystack.Contains(needle, StringComparison.Ordinal))
        {
            throw new AssertionFailedException($"{message} (missing '{needle}' in: {haystack})");
        }
    }

    public static void DoesNotContain(string haystack, string needle, string message)
    {
        if (haystack.Contains(needle, StringComparison.Ordinal))
        {
            throw new AssertionFailedException($"{message} (unexpectedly found '{needle}')");
        }
    }

    public static async Task ThrowsAsync<TException>(Func<Task> action, string message)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new AssertionFailedException(
                $"{message} (expected {typeof(TException).Name}, got {ex.GetType().Name})");
        }

        throw new AssertionFailedException($"{message} (no exception was thrown)");
    }

    private static string Render(object? value) => value switch
    {
        null => "null",
        string text => $"\"{text}\"",
        _ => value.ToString() ?? "<unknown>",
    };
}

internal sealed record TestCase(string Name, Func<Task> Run);

internal static class TestHarness
{
    private static readonly List<TestCase> Cases = new();

    public static void Add(string name, Action test) =>
        Cases.Add(new TestCase(name, () =>
        {
            test();
            return Task.CompletedTask;
        }));

    public static void AddAsync(string name, Func<Task> test) => Cases.Add(new TestCase(name, test));

    public static async Task<int> RunAllAsync()
    {
        var failures = new List<(string Name, string Message)>();
        var passed = 0;

        foreach (var testCase in Cases)
        {
            try
            {
                await testCase.Run().ConfigureAwait(false);
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
        TempWorkspace.Reset();
    }
}

/// <summary>
/// Scratch directory for on-disk fixtures. Lives under the repository's gitignored work/temp
/// folder, falls back to the system temp path, and never points at a real credential store.
/// </summary>
internal static class TempWorkspace
{
    private static string? _root;

    public static string Root => _root ??= CreateRoot();

    public static string NewDirectory(string label)
    {
        var directory = Path.Combine(Root, label + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    public static string WriteFile(string directory, string fileName, string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    public static void Reset() => _root = null;

    private static string CreateRoot()
    {
        var repositoryRoot = FindRepositoryRoot();
        var basePath = repositoryRoot is null
            ? Path.Combine(Path.GetTempPath(), "codexbar-tests")
            : Path.Combine(repositoryRoot, "work", "temp", "codexbar-tests");

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

/// <summary>
/// Deterministic HTTP stub. Records every request so a test can assert what was actually sent
/// (headers, URL) without any network access.
/// </summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responses = new();

    public List<HttpRequestMessage> Requests { get; } = new();

    public int SendCount => Requests.Count;

    public void Enqueue(HttpStatusCode status, string body, params (string Name, string Value)[] headers)
    {
        _responses.Enqueue((_, _) =>
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };

            foreach (var (name, value) in headers)
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }

            return Task.FromResult(response);
        });
    }

    public void EnqueueStatus(HttpStatusCode status) => Enqueue(status, string.Empty);

    public void EnqueueRaw(HttpStatusCode status, string body) =>
        _responses.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)),
        }));

    public void EnqueueFailure(Exception exception) =>
        _responses.Enqueue((_, _) => Task.FromException<HttpResponseMessage>(exception));

    /// <summary>Keeps the request in flight until the caller's token (or the request deadline) fires.</summary>
    public void EnqueueHang() =>
        _responses.Enqueue(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromMinutes(5), token).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("FakeHttpHandler received an unexpected request.");
        }

        return _responses.Dequeue()(request, cancellationToken);
    }
}
