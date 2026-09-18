namespace Norristown.LanguageServer.Protocol;

/// <summary>A request to lay a whole file out.</summary>
/// <param name="TextDocument">The file.</param>
/// <param name="Options">How the client would lay it out.</param>
internal sealed record DocumentFormattingParams(TextDocumentIdentifier TextDocument, FormattingOptions? Options = null);
