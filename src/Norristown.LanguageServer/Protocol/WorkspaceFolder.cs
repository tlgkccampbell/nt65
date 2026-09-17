namespace Norristown.LanguageServer.Protocol;

/// <summary>One folder the client opened.</summary>
internal sealed record WorkspaceFolder(string Uri, string Name);
