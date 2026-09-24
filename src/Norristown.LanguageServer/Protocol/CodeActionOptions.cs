namespace Norristown.LanguageServer.Protocol;

/// <summary>Describes how the server provides code actions.</summary>
/// <param name="CodeActionKinds">
/// The kinds of action the server offers, so that a client can put each kind in its own menu.
/// </param>
internal sealed record CodeActionOptions(IReadOnlyList<string> CodeActionKinds);
