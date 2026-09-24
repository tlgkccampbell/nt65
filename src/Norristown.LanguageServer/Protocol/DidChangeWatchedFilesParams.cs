namespace Norristown.LanguageServer.Protocol;

/// <summary>Parameters of the <c>workspace/didChangeWatchedFiles</c> notification.</summary>
/// <param name="Changes">The files that changed on disk.</param>
internal sealed record DidChangeWatchedFilesParams(IReadOnlyList<FileEvent> Changes);
