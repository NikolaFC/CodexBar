using System.Text;

namespace CodexBar.Core.Internal;

internal enum LocalReadResult
{
    Ok,
    Missing,
    Unreadable,
    TooLarge,
    NotLocalPath,
}

/// <summary>
/// Reads provider credential files from disk only. Remote URLs are rejected outright: a
/// credential path must never cause an authenticated request to an attacker-chosen host.
/// Reads are size-bounded so a runaway file cannot be pulled into memory.
/// </summary>
internal static class LocalFile
{
    public const long DefaultMaxBytes = 4 * 1024 * 1024;

    /// <summary>
    /// True only for paths that cannot leave this machine. UNC/device paths
    /// (<c>\\server\share</c>, <c>\\?\</c>, <c>\\.\</c>) are rejected because a plain
    /// <see cref="FileInfo"/> on them can dispatch an SMB request to an attacker-chosen host.
    /// A <c>file://</c> URI is accepted only when it resolves to a local file.
    /// </summary>
    public static bool IsLocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var trimmed = path.Trim();

        if (TryGetLocalFileUriPath(trimmed, out _))
        {
            return true;
        }

        if (trimmed.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            // A file: URI that failed the local check is remote (UNC or foreign host).
            return false;
        }

        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Any other absolute URI with a non-file scheme (ftp://, ssh://) is rejected.
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            return false;
        }

        // Leading separators mean UNC or a device path; both are off-machine or special.
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) ||
            trimmed.StartsWith("//", StringComparison.Ordinal) ||
            trimmed.StartsWith(@"/\", StringComparison.Ordinal) ||
            trimmed.StartsWith(@"\/", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Resolves a local <c>file://</c> URI to a filesystem path. Returns false for UNC shares,
    /// <c>file://host/...</c> URIs, and anything that is not a file URI.
    /// </summary>
    private static bool TryGetLocalFileUriPath(string path, out string localPath)
    {
        localPath = string.Empty;

        if (!path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Uri.TryCreate(path, UriKind.Absolute, out var uri) || !uri.IsFile)
        {
            return false;
        }

        if (uri.IsUnc ||
            (!string.IsNullOrEmpty(uri.Host) && !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var resolved = uri.LocalPath;
        if (string.IsNullOrWhiteSpace(resolved))
        {
            return false;
        }

        localPath = resolved;
        return true;
    }

    /// <summary>
    /// Expands a leading <c>~</c>, converts a local <c>file://</c> URI, and resolves to an
    /// absolute on-machine path. Throws for anything <see cref="IsLocalPath"/> rejects.
    /// </summary>
    public static string Normalize(string path)
    {
        var trimmed = path.Trim();

        if (TryGetLocalFileUriPath(trimmed, out var fromUri))
        {
            trimmed = fromUri;
        }

        if (trimmed == "~" ||
            trimmed.StartsWith("~/", StringComparison.Ordinal) ||
            trimmed.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var remainder = trimmed.Length <= 2 ? string.Empty : trimmed[2..];
            trimmed = string.IsNullOrEmpty(remainder) ? home : Path.Combine(home, remainder);
        }

        var full = Path.GetFullPath(trimmed);

        // GetFullPath can still produce a UNC path when the process CWD is a network share.
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.StartsWith("//", StringComparison.Ordinal))
        {
            throw new NotSupportedException("Network paths are not supported for credential files.");
        }

        return full;
    }

    public static LocalReadResult TryReadBounded(string path, out byte[] bytes, long maxBytes = DefaultMaxBytes)
    {
        bytes = Array.Empty<byte>();

        if (!IsLocalPath(path))
        {
            return LocalReadResult.NotLocalPath;
        }

        string full;
        try
        {
            full = Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return LocalReadResult.NotLocalPath;
        }

        try
        {
            var info = new FileInfo(full);
            if (!info.Exists)
            {
                return LocalReadResult.Missing;
            }

            if (info.Length > maxBytes)
            {
                return LocalReadResult.TooLarge;
            }

            // FileShare.ReadWrite keeps a concurrently rewriting CLI owner (codex/claude login)
            // from failing the read; we only ever hold our own copy of the bytes.
            using var stream = new FileStream(
                full,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 4096,
                FileOptions.SequentialScan);

            if (stream.Length > maxBytes)
            {
                return LocalReadResult.TooLarge;
            }

            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    return LocalReadResult.TooLarge;
                }

                buffer.Write(chunk, 0, read);
            }

            bytes = buffer.ToArray();
            return LocalReadResult.Ok;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return LocalReadResult.Missing;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException)
        {
            return LocalReadResult.Unreadable;
        }
    }

    public static string DecodeUtf8(byte[] bytes) => new UTF8Encoding(false).GetString(bytes);
}
