namespace Norristown.LanguageServer.Protocol;

/// <summary>Describes how the server provides inlay hints.</summary>
/// <param name="ResolveProvider">
/// Whether hints are sent without their tooltips, which a second request fills in. nt65 always
/// sends each hint's tooltip up front, because the analysis already has the sentence and a second
/// request would gain nothing.
/// </param>
internal sealed record InlayHintOptions(bool ResolveProvider);
