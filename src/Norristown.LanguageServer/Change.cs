namespace Norristown.LanguageServer;

/// <summary>
/// A change an editor may offer, before it is spelled as the protocol spells it: what to call
/// it, which menu it belongs in and what it writes.
/// </summary>
/// <param name="Title">What the client offers it as.</param>
/// <param name="Kind">Which menu it belongs in, one of <see cref="CodeActionKinds"/>.</param>
/// <param name="Edits">What it writes, in any order and across any files.</param>
/// <param name="For">The diagnostic it fixes, or null for a change nothing reported.</param>
/// <param name="Preferred">
/// Whether it is the change to apply without asking which: false where it is one of several
/// readings of the same line, and the programmer is the one who knows which was meant.
/// </param>
internal sealed record Change(
    string Title,
    string Kind,
    IReadOnlyList<Edit> Edits,
    Diagnostic? For = null,
    bool? Preferred = null);
