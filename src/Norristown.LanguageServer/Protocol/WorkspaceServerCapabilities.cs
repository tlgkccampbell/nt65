namespace Norristown.LanguageServer.Protocol;

/// <summary>What the server can do about the workspace itself, rather than about a document in it.</summary>
/// <param name="WorkspaceFolders">What it does about the folders the client has open.</param>
internal sealed record WorkspaceServerCapabilities(WorkspaceFoldersServerCapabilities WorkspaceFolders);
