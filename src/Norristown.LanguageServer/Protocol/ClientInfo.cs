namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents the client's name and version, as sent with <c>initialize</c>.</summary>
/// <param name="Name">The client's name.</param>
/// <param name="Version">The client's version, or null.</param>
internal sealed record ClientInfo(string Name, string? Version);
