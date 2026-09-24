namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Represents a kind of file the server wants to be notified about before the file moves.
/// </summary>
/// <param name="Pattern">The paths the filter matches.</param>
/// <param name="Scheme">The URI scheme the filter is limited to, or null for any scheme.</param>
internal sealed record FileOperationFilter(FileOperationPattern Pattern, string? Scheme = null);
