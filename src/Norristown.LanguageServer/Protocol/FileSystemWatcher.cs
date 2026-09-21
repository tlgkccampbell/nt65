namespace Norristown.LanguageServer.Protocol;

/// <summary>One pattern a client is asked to watch.</summary>
/// <param name="GlobPattern">What it matches.</param>
internal sealed record FileSystemWatcher(string GlobPattern);
