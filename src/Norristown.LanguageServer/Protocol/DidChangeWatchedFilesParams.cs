namespace Norristown.LanguageServer.Protocol;

internal sealed record DidChangeWatchedFilesParams(IReadOnlyList<FileEvent> Changes);
