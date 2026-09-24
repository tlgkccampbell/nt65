namespace Norristown.LanguageServer.Protocol;

/// <summary>Describes the files the server is notified about for one file operation.</summary>
/// <param name="Filters">The kinds of file the server wants to be notified about.</param>
internal sealed record FileOperationRegistrationOptions(IReadOnlyList<FileOperationFilter> Filters);
