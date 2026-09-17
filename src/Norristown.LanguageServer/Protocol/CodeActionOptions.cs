namespace Norristown.LanguageServer.Protocol;

/// <summary>How the server answers with code actions.</summary>
/// <param name="CodeActionKinds">The kinds it offers, so that a client can put each in its own menu.</param>
internal sealed record CodeActionOptions(IReadOnlyList<string> CodeActionKinds);
