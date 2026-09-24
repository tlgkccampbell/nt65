namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents the server's answer to the <c>initialize</c> request.</summary>
/// <param name="Capabilities">What the server can do.</param>
/// <param name="ServerInfo">The server's name and version.</param>
internal sealed record InitializeResult(ServerCapabilities Capabilities, ServerInfo ServerInfo);
