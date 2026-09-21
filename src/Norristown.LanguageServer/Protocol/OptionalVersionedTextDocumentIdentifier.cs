namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// A document an edit is against, and the revision it was worked out from. A null version is a
/// file the client has not opened, which nothing can have changed under the edit.
/// </summary>
/// <param name="Uri">How the client names the file.</param>
/// <param name="Version">The revision the edit was worked out against, or null for a file nobody has open.</param>
internal sealed record OptionalVersionedTextDocumentIdentifier(string Uri, int? Version);
