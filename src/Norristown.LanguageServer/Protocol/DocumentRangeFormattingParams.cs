namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Parameters of the <c>textDocument/rangeFormatting</c> request, which asks to format part of a
/// file.
/// </summary>
/// <param name="TextDocument">The file.</param>
/// <param name="Range">The lines to format. The rest of the file is left unchanged.</param>
/// <param name="Options">The client's formatting preferences.</param>
internal sealed record DocumentRangeFormattingParams(
    TextDocumentIdentifier TextDocument, Range Range, FormattingOptions? Options = null);
