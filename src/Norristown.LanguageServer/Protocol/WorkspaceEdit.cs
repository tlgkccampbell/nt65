namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Represents changes to make, grouped by document. A client that declared <c>documentChanges</c>
/// also receives the same edits in a second form, each naming the version it was computed
/// against, and uses those. An edit computed against a buffer that has since changed is then
/// refused rather than applied in the wrong place. A client that declared nothing receives only
/// <see cref="Changes"/>, which is the shape every client understands.
/// </summary>
/// <param name="Changes">The edits for each document URI.</param>
/// <param name="DocumentChanges">The same edits, each against a named version, or null.</param>
internal sealed record WorkspaceEdit(
    IReadOnlyDictionary<string, IReadOnlyList<TextEdit>> Changes,
    IReadOnlyList<TextDocumentEdit>? DocumentChanges = null);
