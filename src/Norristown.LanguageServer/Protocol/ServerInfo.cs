namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Represents the server's name and version, as sent in the answer to <c>initialize</c>.
/// </summary>
/// <param name="Name">The server's name.</param>
/// <param name="Version">The server's version.</param>
internal sealed record ServerInfo(string Name, string Version);
