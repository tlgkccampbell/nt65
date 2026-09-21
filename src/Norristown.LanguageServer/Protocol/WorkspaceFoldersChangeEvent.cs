namespace Norristown.LanguageServer.Protocol;

/// <summary>Which folders joined the workspace and which left it.</summary>
/// <param name="Added">The folders now in the workspace that were not before.</param>
/// <param name="Removed">The folders no longer in it.</param>
internal sealed record WorkspaceFoldersChangeEvent(
    IReadOnlyList<WorkspaceFolder> Added, IReadOnlyList<WorkspaceFolder> Removed);
