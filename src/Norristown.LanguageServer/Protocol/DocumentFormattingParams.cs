namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Parameters of the <c>textDocument/formatting</c> request, which asks to format a whole file.
/// </summary>
/// <param name="TextDocument">The file.</param>
/// <param name="Options">The client's formatting preferences.</param>
internal sealed record DocumentFormattingParams(TextDocumentIdentifier TextDocument, FormattingOptions? Options = null);
