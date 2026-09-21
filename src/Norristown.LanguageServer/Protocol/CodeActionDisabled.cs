namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Why a change is offered and cannot be applied. A client shows it greyed with the reason,
/// which is how a rewrite that would change what the line means says so rather than quietly
/// not being there.
/// </summary>
/// <param name="Reason">What stands in the way, as a sentence.</param>
internal sealed record CodeActionDisabled(string Reason);
