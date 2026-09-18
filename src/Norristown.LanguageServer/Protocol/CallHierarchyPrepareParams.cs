namespace Norristown.LanguageServer.Protocol;

/// <summary>A request for the routines a call hierarchy may start from at a place in a file.</summary>
/// <param name="TextDocument">The file.</param>
/// <param name="Position">Where in it.</param>
internal sealed record CallHierarchyPrepareParams(TextDocumentIdentifier TextDocument, Position Position);
