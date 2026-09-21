namespace Norristown.LanguageServer.Protocol;

/// <summary>How the server hints.</summary>
/// <param name="ResolveProvider">
/// Whether a hint is answered in two parts. Every hint nt65 writes carries its tooltip already:
/// it is a sentence the analysis has in hand, and a second request for it would buy nothing.
/// </param>
internal sealed record InlayHintOptions(bool ResolveProvider);
