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
/// <param name="Names">
/// The name it writes for the programmer to replace, or null for a change that leaves none.
/// </param>
/// <param name="Renames">
/// A name already written that the change asks the programmer to replace, and its whole work,
/// for a change that writes nothing: the editor puts the caret there and starts a rename.
/// </param>
/// <param name="Refused">
/// Why it cannot be applied here, or null for one that can. A change that would change what the
/// line means is offered greyed with the reason rather than left out, so that looking for it
/// finds the reason.
/// </param>
internal sealed record Change(
    string Title,
    string Kind,
    IReadOnlyList<Edit> Edits,
    Diagnostic? For = null,
    bool? Preferred = null,
    Change.Placeholder? Names = null,
    Span? Renames = null,
    string? Refused = null)
{
    /// <summary>
    /// A name a change writes because it has to write something, and the programmer is the one
    /// who knows what it should be: which edit's text holds it, and where in that text it
    /// starts. It is turned into a position in the file as the change leaves it, so that the
    /// editor can put the caret on the name and start a rename.
    /// </summary>
    /// <param name="In">The edit whose text holds the name.</param>
    /// <param name="At">Where the name starts in that edit's text.</param>
    internal sealed record Placeholder(Edit In, int At);
}
