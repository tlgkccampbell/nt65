namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents the paths a file-operation filter matches.</summary>
/// <param name="Glob">The glob pattern a path is matched against.</param>
/// <param name="Matches">Whether only files or only folders match, or null for both.</param>
internal sealed record FileOperationPattern(string Glob, string? Matches = null);
