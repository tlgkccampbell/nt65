namespace Norristown.LanguageServer.Protocol;

/// <summary>How the server answers with links.</summary>
/// <param name="ResolveProvider">Whether a link comes back without its target, to be asked for later.</param>
internal sealed record DocumentLinkOptions(bool ResolveProvider);
