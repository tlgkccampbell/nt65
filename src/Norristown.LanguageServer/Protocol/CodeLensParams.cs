namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Parameters of the <c>textDocument/codeLens</c> request, which asks for every lens in a file.
/// </summary>
/// <param name="TextDocument">The file.</param>
internal sealed record CodeLensParams(TextDocumentIdentifier TextDocument);
