namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Why an offered change cannot be applied. A client shows the change greyed out with the
/// reason, so a rewrite that would alter what the line means explains itself instead of
/// silently missing from the menu.
/// </summary>
/// <param name="Reason">What prevents the change, as a sentence.</param>
internal sealed record CodeActionDisabled(string Reason);
