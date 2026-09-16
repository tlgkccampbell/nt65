namespace Norristown.LanguageServer.Protocol;

/// <summary>Names a document and the revision of it a message is about.</summary>
/// <param name="Uri">The document's URI.</param>
/// <param name="Version">The revision, which the client increments on every change.</param>
internal sealed record VersionedTextDocumentIdentifier(string Uri, int Version);
