namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// <c>client/unregisterCapability</c>: what the server is taking back. The protocol spells the
/// field <c>unregisterations</c>, and a client reads what the protocol says.
/// </summary>
/// <param name="Unregisterations">Each thing it is taking back.</param>
internal sealed record UnregistrationParams(IReadOnlyList<Unregistration> Unregisterations);
