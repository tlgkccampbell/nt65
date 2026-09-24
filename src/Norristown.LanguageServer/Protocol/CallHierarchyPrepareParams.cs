namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Parameters of the <c>textDocument/prepareCallHierarchy</c> request, which asks for the routines
/// a call hierarchy can start from at a position in a file.
/// </summary>
/// <param name="TextDocument">The file.</param>
/// <param name="Position">The position in the file.</param>
internal sealed record CallHierarchyPrepareParams(TextDocumentIdentifier TextDocument, Position Position);
