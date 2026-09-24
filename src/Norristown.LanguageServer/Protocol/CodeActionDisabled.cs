namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Represents the reason an offered change cannot be applied. A client shows the change greyed
/// out with the reason, so a rewrite that would alter the meaning of the line explains itself
/// instead of silently missing from the menu.
/// </summary>
/// <param name="Reason">A sentence that states what prevents the change.</param>
internal sealed record CodeActionDisabled(string Reason);
