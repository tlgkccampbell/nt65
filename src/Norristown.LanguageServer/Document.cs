using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// One document the client has open, at the revision the client last sent. Its meaning is
/// worked out by the <see cref="Workspace"/> rather than here, because a name it uses may be
/// exported by another file.
/// </summary>
internal sealed class Document(string uri, int version, SyntaxTree tree)
{
    /// <summary>The document's URI, which is how the client names it.</summary>
    public string Uri { get; } = uri;

    /// <summary>The revision this tree was parsed from.</summary>
    public int Version { get; } = version;

    /// <summary>The document's syntax.</summary>
    public SyntaxTree Tree { get; } = tree;
}
