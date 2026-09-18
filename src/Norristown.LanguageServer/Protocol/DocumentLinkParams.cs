namespace Norristown.LanguageServer.Protocol;

/// <summary>A request for every link in a file.</summary>
/// <param name="TextDocument">The file.</param>
internal sealed record DocumentLinkParams(TextDocumentIdentifier TextDocument);
