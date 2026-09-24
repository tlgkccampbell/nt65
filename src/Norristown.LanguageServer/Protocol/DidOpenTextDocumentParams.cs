namespace Norristown.LanguageServer.Protocol;

/// <summary>Parameters of the <c>textDocument/didOpen</c> notification.</summary>
/// <param name="TextDocument">The document and its text.</param>
internal sealed record DidOpenTextDocumentParams(TextDocumentItem TextDocument);
