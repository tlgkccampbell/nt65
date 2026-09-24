namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Parameters of the <c>textDocument/documentLink</c> request, which asks for every link in a file.
/// </summary>
/// <param name="TextDocument">The file.</param>
internal sealed record DocumentLinkParams(TextDocumentIdentifier TextDocument);
