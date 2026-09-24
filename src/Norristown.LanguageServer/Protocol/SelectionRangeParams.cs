namespace Norristown.LanguageServer.Protocol;

/// <summary>Parameters of the <c>textDocument/selectionRange</c> request.</summary>
/// <param name="TextDocument">The document the carets are in.</param>
/// <param name="Positions">The carets, each of which is answered with its own chain of ranges.</param>
internal sealed record SelectionRangeParams(
    TextDocumentIdentifier TextDocument, IReadOnlyList<Position> Positions);