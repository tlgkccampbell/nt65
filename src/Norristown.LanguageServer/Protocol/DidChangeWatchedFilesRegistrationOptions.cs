namespace Norristown.LanguageServer.Protocol;

/// <summary>What a client is asked to watch, for <c>workspace/didChangeWatchedFiles</c>.</summary>
/// <param name="Watchers">The patterns to watch.</param>
internal sealed record DidChangeWatchedFilesRegistrationOptions(IReadOnlyList<FileSystemWatcher> Watchers);
