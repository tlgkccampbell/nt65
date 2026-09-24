namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents the folders added to and removed from the workspace.</summary>
/// <param name="Added">The folders now in the workspace that were not before.</param>
/// <param name="Removed">The folders no longer in the workspace.</param>
internal sealed record WorkspaceFoldersChangeEvent(
    IReadOnlyList<WorkspaceFolder> Added, IReadOnlyList<WorkspaceFolder> Removed);
