namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Parameters of the <c>client/unregisterCapability</c> request, which lists the registrations the
/// server is removing. The protocol misspells the field as <c>unregisterations</c>, and clients
/// expect that spelling, so it is kept.
/// </summary>
/// <param name="Unregisterations">The registrations to remove.</param>
internal sealed record UnregistrationParams(IReadOnlyList<Unregistration> Unregisterations);
