using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace CodexBar.Core.Internal;

/// <summary>
/// Raised when a provider response exceeds <see cref="HttpDefaults.MaxResponseBytes"/>. The body is
/// discarded rather than truncated, so a partial payload can never be parsed as a valid success.
/// </summary>
internal sealed class ResponseTooLargeException : Exception
{
    public ResponseTooLargeException()
        : base("Provider response exceeded the maximum accepted size.")
    {
    }
}

/// <summary>Testable HTTP defaults shared by both providers.</summary>
public static class HttpDefaults
{
    /// <summary>Default per-request deadline in seconds.</summary>
    public const int DefaultTimeoutSeconds = 30;

    public const long MaxResponseBytes = 4 * 1024 * 1024;

    /// <summary>
    /// The handler CodexBar uses when the caller does not supply an <see cref="HttpClient"/>.
    /// Automatic redirects stay off so a bearer token can never be replayed to a redirect target.
    /// </summary>
    public static HttpClientHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
    };

    public static HttpClient CreateClient() =>
        // Per-request deadlines are enforced with a linked CancellationTokenSource so a caller's
        // cancellation stays distinguishable from a timeout.
        new(CreateHandler(), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
}

internal sealed class HttpTransport : IDisposable
{
    private readonly bool _ownsClient;

    private HttpTransport(HttpClient client, bool ownsClient)
    {
        Client = client;
        _ownsClient = ownsClient;
    }

    public HttpClient Client { get; }

    public static HttpTransport Create(HttpClient? client) =>
        client is null
            ? new HttpTransport(HttpDefaults.CreateClient(), ownsClient: true)
            : new HttpTransport(client, ownsClient: false);

    /// <summary>Sends a request under a per-request deadline and returns a size-bounded body.</summary>
    public async Task<HttpResult> SendAsync(
        HttpRequestMessage request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        using var response = await Client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
            .ConfigureAwait(false);

        var body = await ReadBoundedAsync(response, linked.Token).ConfigureAwait(false);
        return new HttpResult(response.StatusCode, response.Headers, body);
    }

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[32 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > HttpDefaults.MaxResponseBytes)
            {
                // Fail closed: a truncated body could still parse as a plausible prefix.
                throw new ResponseTooLargeException();
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            Client.Dispose();
        }
    }
}

internal sealed record HttpResult(HttpStatusCode StatusCode, HttpResponseHeaders Headers, string Body)
{
    public string? Header(string name) =>
        Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    public bool BodyContains(string needle) => Body.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
