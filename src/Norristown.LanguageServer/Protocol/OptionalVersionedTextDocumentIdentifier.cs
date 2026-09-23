namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// The document an edit applies to, and the revision it was computed from. A null version means
/// the client does not have the file open, so no unsaved change in the editor can conflict
/// with the edit.
/// </summary>
/// <param name="Uri">How the client names the file.</param>
/// <param name="Version">The revision the edit was worked out against, or null for a file nobody has open.</param>
internal sealed record OptionalVersionedTextDocumentIdentifier(string Uri, int? Version);
