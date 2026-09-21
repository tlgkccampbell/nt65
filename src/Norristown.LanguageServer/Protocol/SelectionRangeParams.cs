namespace Norristown.LanguageServer.Protocol;

/// <summary><c>textDocument/selectionRange</c>.</summary>
/// <param name="TextDocument">The document the carets are in.</param>
/// <param name="Positions">Each caret, which is answered with a chain of its own.</param>
internal sealed record SelectionRangeParams(
    TextDocumentIdentifier TextDocument, IReadOnlyList<Position> Positions);