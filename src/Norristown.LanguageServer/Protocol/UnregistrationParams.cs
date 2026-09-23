namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// <c>client/unregisterCapability</c>: what the server is taking back. The protocol misspells
/// the field as <c>unregisterations</c>, and clients expect that spelling, so it is kept.
/// </summary>
/// <param name="Unregisterations">Each thing it is taking back.</param>
internal sealed record UnregistrationParams(IReadOnlyList<Unregistration> Unregisterations);
