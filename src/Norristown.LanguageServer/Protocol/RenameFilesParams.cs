namespace Norristown.LanguageServer.Protocol;

/// <summary><c>workspace/willRenameFiles</c>: the files about to move.</summary>
/// <param name="Files">Each file, where it is and where it is going.</param>
internal sealed record RenameFilesParams(IReadOnlyList<FileRename> Files);
