namespace Norristown.LanguageServer.Protocol;

/// <summary>Identifies a document and the version of it that a message is about.</summary>
/// <param name="Uri">The document's URI.</param>
/// <param name="Version">The version, which the client increments on every change.</param>
internal sealed record VersionedTextDocumentIdentifier(string Uri, int Version);
