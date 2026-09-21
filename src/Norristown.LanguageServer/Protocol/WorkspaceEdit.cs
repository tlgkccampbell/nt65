namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Changes to make, by document. A client that declared <c>documentChanges</c> is given the
/// same edits a second way as well, each naming the revision they were worked out against, and
/// uses those: one worked out against a buffer that has since changed is then refused rather
/// than written into the wrong place. A client that declared nothing has only
/// <see cref="Changes"/>, which is the shape every client understands.
/// </summary>
/// <param name="Changes">The edits for each document URI.</param>
/// <param name="DocumentChanges">The same edits, each against a named revision, or null.</param>
internal sealed record WorkspaceEdit(
    IReadOnlyDictionary<string, IReadOnlyList<TextEdit>> Changes,
    IReadOnlyList<TextDocumentEdit>? DocumentChanges = null);
