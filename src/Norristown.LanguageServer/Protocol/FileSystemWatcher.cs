namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents a pattern a client is asked to watch.</summary>
/// <param name="GlobPattern">The glob pattern of the files to watch.</param>
internal sealed record FileSystemWatcher(string GlobPattern);
