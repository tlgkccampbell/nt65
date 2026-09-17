namespace Norristown.LanguageServer.Protocol;

/// <summary>How the server answers with lenses.</summary>
/// <param name="ResolveProvider">Whether a lens comes back without its command, to be asked for later.</param>
internal sealed record CodeLensOptions(bool ResolveProvider);
