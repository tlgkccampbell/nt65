namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Represents a capability the server registers with the client after they have connected.
/// </summary>
/// <param name="Id">An identifier for the registration, so that it can be unregistered later.</param>
/// <param name="Method">The method the registration is for.</param>
/// <param name="RegisterOptions">The registration options, whose shape depends on the method.</param>
internal sealed record Registration(string Id, string Method, object? RegisterOptions);
