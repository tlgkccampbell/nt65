namespace Norristown.LanguageServer.Protocol;

/// <summary><c>textDocument/didOpen</c>.</summary>
/// <param name="TextDocument">The document and its text.</param>
internal sealed record DidOpenTextDocumentParams(TextDocumentItem TextDocument);
