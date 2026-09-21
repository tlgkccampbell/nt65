namespace Norristown.LanguageServer.Protocol;

/// <summary>What a server is told about, for one file operation.</summary>
/// <param name="Filters">The kinds of file it wants to hear about.</param>
internal sealed record FileOperationRegistrationOptions(IReadOnlyList<FileOperationFilter> Filters);
