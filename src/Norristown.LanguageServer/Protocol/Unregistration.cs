namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents a registration the server is removing.</summary>
/// <param name="Id">The identifier the registration was given.</param>
/// <param name="Method">The method the registration was for.</param>
internal sealed record Unregistration(string Id, string Method);
