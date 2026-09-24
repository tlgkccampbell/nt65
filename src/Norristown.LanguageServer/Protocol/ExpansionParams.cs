namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Parameters of the <c>nt65/expansion</c> request, which asks what the macro call at a position
/// expands to, rendered as nt65 source.
/// </summary>
/// <param name="TextDocument">The file the call is in.</param>
/// <param name="Position">The position of the call. Any position on the call's line is accepted.</param>
/// <param name="Into">
/// A path of indices. At each level, an index selects which unexpanded call to expand further, by
/// its position among the calls left unexpanded at that level. An empty or absent path expands
/// only the first level, which is what a view opens with.
/// </param>
/// <param name="All">Whether to expand every nested call, at any depth.</param>
internal sealed record ExpansionParams(
    TextDocumentIdentifier TextDocument, Position Position, IReadOnlyList<int>? Into = null, bool All = false);
