namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents a folder the client opened.</summary>
/// <param name="Uri">The folder's URI.</param>
/// <param name="Name">The folder's name.</param>
internal sealed record WorkspaceFolder(string Uri, string Name);
