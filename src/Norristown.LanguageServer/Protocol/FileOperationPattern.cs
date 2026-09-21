namespace Norristown.LanguageServer.Protocol;

/// <summary>Which paths a file-operation filter matches.</summary>
/// <param name="Glob">The pattern a path is matched against.</param>
/// <param name="Matches">Whether only a file or only a folder matches, or null for either.</param>
internal sealed record FileOperationPattern(string Glob, string? Matches = null);
