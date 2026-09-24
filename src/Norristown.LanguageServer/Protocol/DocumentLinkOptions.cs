namespace Norristown.LanguageServer.Protocol;

/// <summary>Describes how the server provides document links.</summary>
/// <param name="ResolveProvider">
/// Whether a link is returned without its target, which the client requests later.
/// </param>
internal sealed record DocumentLinkOptions(bool ResolveProvider);
