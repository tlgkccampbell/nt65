using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Represents one document the client has open, at the revision the client last sent. The
/// <see cref="Workspace"/> analyzes the document rather than this class, because a name it uses
/// may be exported by another file.
/// </summary>
internal sealed class Document(string uri, int version, SyntaxTree tree)
{
    /// <summary>Gets the document's URI, which is how the client names it.</summary>
    public string Uri { get; } = uri;

    /// <summary>Gets the revision this tree was parsed from.</summary>
    public int Version { get; } = version;

    /// <summary>Gets the document's syntax tree.</summary>
    public SyntaxTree Tree { get; } = tree;
}
