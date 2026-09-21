namespace Norristown.LanguageServer.Protocol;

/// <summary>One kind of file a server wants to hear about before it moves.</summary>
/// <param name="Pattern">Which paths it matches.</param>
/// <param name="Scheme">The URI scheme it is limited to, or null for any.</param>
internal sealed record FileOperationFilter(FileOperationPattern Pattern, string? Scheme = null);
