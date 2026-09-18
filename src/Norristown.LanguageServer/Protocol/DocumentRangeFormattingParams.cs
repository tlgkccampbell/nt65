namespace Norristown.LanguageServer.Protocol;

/// <summary>A request to lay part of a file out.</summary>
/// <param name="TextDocument">The file.</param>
/// <param name="Range">The lines to lay out; the rest of the file is left where it is.</param>
/// <param name="Options">How the client would lay it out.</param>
internal sealed record DocumentRangeFormattingParams(
    TextDocumentIdentifier TextDocument, Range Range, FormattingOptions? Options = null);
