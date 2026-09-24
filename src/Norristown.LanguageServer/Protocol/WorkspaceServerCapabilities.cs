namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Describes what the server can do with the workspace itself, as opposed to a document in it.
/// </summary>
/// <param name="WorkspaceFolders">What the server does with the folders the client has open.</param>
/// <param name="FileOperations">What the server does when a file moves, or null when it does nothing.</param>
internal sealed record WorkspaceServerCapabilities(
    WorkspaceFoldersServerCapabilities? WorkspaceFolders,
    FileOperationsServerCapabilities? FileOperations = null);
