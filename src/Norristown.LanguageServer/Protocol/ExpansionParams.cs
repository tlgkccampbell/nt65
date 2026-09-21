namespace Norristown.LanguageServer.Protocol;

/// <summary><c>nt65/expansion</c>: what the macro call at a place becomes, written as nt65.</summary>
/// <param name="TextDocument">The file the call is in.</param>
/// <param name="Position">Where in it, anywhere on the call's line.</param>
/// <param name="Into">
/// Which call to write out further at each level, by its place among the calls left as calls at
/// that level. Empty, or absent, for the one level a view opens with.
/// </param>
/// <param name="All">Whether to write out every call, however deep.</param>
internal sealed record ExpansionParams(
    TextDocumentIdentifier TextDocument, Position Position, IReadOnlyList<int>? Into = null, bool All = false);
