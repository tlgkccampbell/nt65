namespace Norristown.LanguageServer.Protocol;

/// <summary>The folders the client has open changed.</summary>
/// <param name="Event">What joined and what left.</param>
internal sealed record DidChangeWorkspaceFoldersParams(WorkspaceFoldersChangeEvent Event);
