namespace Norristown.LanguageServer.Protocol;

/// <summary>How the server hints.</summary>
/// <param name="ResolveProvider">
/// Whether hints are sent without their tooltips, to be filled in by a second request. nt65
/// always sends each hint's tooltip up front: the analysis already has the sentence, so a
/// second request would gain nothing.
/// </param>
internal sealed record InlayHintOptions(bool ResolveProvider);
