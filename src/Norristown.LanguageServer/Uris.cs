using Norristown.Standard;

namespace Norristown.LanguageServer;

/// <summary>
/// Converts between the URIs the protocol names files by and the logical paths, with <c>/</c>
/// separators, that diagnostics and output carry. Both directions are here so that each undoes
/// the other. The form of a URI a particular client uses for a file is the
/// <see cref="Workspace"/>'s concern, since it depends on what the client has sent.
/// </summary>
internal static class Uris
{
    /// <summary>
    /// The prefix of the URI of a module that comes with nt65. No file holds such a module, so
    /// the client asks the server for its text (<c>nt65/standardModule</c>) and shows it
    /// read-only.
    /// </summary>
    private const string StandardScheme = "nt65:/";

    /// <summary>
    /// Returns the logical path a URI names, with <c>/</c> separators, which is the form that
    /// diagnostics and output carry. A URI that is not a file is returned unchanged.
    /// </summary>
    public static string ToPath(string uri)
    {
        if (uri.StartsWith(StandardScheme, StringComparison.Ordinal))
            return StandardModules.PathOf(uri[StandardScheme.Length..]);
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile)
            return uri;
        // VS Code escapes a drive's colon, `file:///c%3A/src`, which .NET does not take for a
        // drive and gives back as `/c:/src`.
        var path = parsed.LocalPath.Replace('\\', '/');
        return path.Length >= 3 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] == ':' ? path[1..] : path;
    }

    /// <summary>
    /// Converts a logical path back to a URI, as for a diagnostic that points into another file.
    /// A Windows path parses as an absolute URI, drive letter and all, wherever nt65 is running.
    /// A rooted Unix path does not parse as a URI on any host, so a file URI is built for it. A
    /// relative path is one the editor supplied and is returned unchanged.
    /// </summary>
    public static string ToUri(string path) =>
        StandardModules.IsStandard(path) ? StandardScheme + StandardModules.FileOf(path)
        : Uri.TryCreate(path, UriKind.Absolute, out var uri) ? uri.AbsoluteUri
            : path.StartsWith('/')
                ? new UriBuilder(Uri.UriSchemeFile, string.Empty) { Path = path }.Uri.AbsoluteUri
                : path;
}
