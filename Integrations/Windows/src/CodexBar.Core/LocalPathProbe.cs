using CodexBar.Core.Internal;

namespace CodexBar.Core;

/// <summary>
/// Validates user-supplied credential paths before they reach a provider or the settings UI.
///
/// A credential path must name a file on this machine. Network shares, device paths, and remote
/// URLs are rejected because opening them can dispatch a request, and an authenticated request,
/// to a host the user did not choose.
/// </summary>
public static class LocalPathProbe
{
    /// <summary>True when the path names a local file and cannot reach another machine.</summary>
    public static bool IsLocal(string? path) => LocalFile.IsLocalPath(path);

    /// <summary>
    /// Expands a leading <c>~</c>, converts a local <c>file://</c> URI, and returns an absolute path.
    /// Throws for anything <see cref="IsLocal"/> rejects.
    /// </summary>
    public static string Normalize(string path) => LocalFile.Normalize(path);
}
