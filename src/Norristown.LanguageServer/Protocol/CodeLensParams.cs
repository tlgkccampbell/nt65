namespace Norristown.LanguageServer.Protocol;

/// <summary>A request for every lens in a file.</summary>
/// <param name="TextDocument">The file.</param>
internal sealed record CodeLensParams(TextDocumentIdentifier TextDocument);
