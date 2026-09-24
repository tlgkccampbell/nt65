namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Parameters of the <c>workspace/willRenameFiles</c> request, which lists the files about to move.
/// </summary>
/// <param name="Files">Each file's current and new URI.</param>
internal sealed record RenameFilesParams(IReadOnlyList<FileRename> Files);
