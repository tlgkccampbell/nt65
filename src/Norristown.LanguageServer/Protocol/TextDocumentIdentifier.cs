namespace Norristown.LanguageServer.Protocol;

/// <summary>Names a document.</summary>
/// <param name="Uri">The document's URI.</param>
internal sealed record TextDocumentIdentifier(string Uri);
