namespace Norristown.LanguageServer.Protocol;

/// <summary><c>nt65/expansion</c>: what the macro call at a position expands to, written as nt65.</summary>
/// <param name="TextDocument">The file the call is in.</param>
/// <param name="Position">Where in it; anywhere on the call's line will do.</param>
/// <param name="Into">
/// A path of indices: at each level, which unexpanded call to expand further, by its position
/// among the calls left unexpanded at that level. Empty or absent to expand only the first
/// level, which is what a view opens with.
/// </param>
/// <param name="All">Whether to expand every nested call, however deep.</param>
internal sealed record ExpansionParams(
    TextDocumentIdentifier TextDocument, Position Position, IReadOnlyList<int>? Into = null, bool All = false);
