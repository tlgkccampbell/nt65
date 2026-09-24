namespace Norristown.LanguageServer.Protocol;

/// <summary>Describes how the server provides code lenses.</summary>
/// <param name="ResolveProvider">
/// Whether a lens is returned without its command, which the client requests later.
/// </param>
internal sealed record CodeLensOptions(bool ResolveProvider);
