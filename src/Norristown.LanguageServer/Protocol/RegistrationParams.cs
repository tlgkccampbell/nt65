namespace Norristown.LanguageServer.Protocol;

/// <summary><c>client/registerCapability</c>: what the server now asks the client to do.</summary>
/// <param name="Registrations">Each thing it asks for.</param>
internal sealed record RegistrationParams(IReadOnlyList<Registration> Registrations);
