namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Parameters of the <c>workspace/didChangeWorkspaceFolders</c> notification, which reports that
/// the folders the client has open changed.
/// </summary>
/// <param name="Event">The folders that were added and removed.</param>
internal sealed record DidChangeWorkspaceFoldersParams(WorkspaceFoldersChangeEvent Event);
