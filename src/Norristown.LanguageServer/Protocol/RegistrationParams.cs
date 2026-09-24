namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Parameters of the <c>client/registerCapability</c> request, which lists the capabilities the
/// server now registers.
/// </summary>
/// <param name="Registrations">The registrations.</param>
internal sealed record RegistrationParams(IReadOnlyList<Registration> Registrations);
