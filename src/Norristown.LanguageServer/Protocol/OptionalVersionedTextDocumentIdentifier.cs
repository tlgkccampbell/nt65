namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Identifies the document an edit applies to, and the version the edit was computed from. A null
/// version means the client does not have the file open, so no unsaved change in the editor can
/// conflict with the edit.
/// </summary>
/// <param name="Uri">The client's URI for the file.</param>
/// <param name="Version">
/// The version the edit was computed against, or null for a file that no client has open.
/// </param>
internal sealed record OptionalVersionedTextDocumentIdentifier(string Uri, int? Version);
