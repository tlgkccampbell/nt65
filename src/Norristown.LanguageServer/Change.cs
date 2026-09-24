namespace Norristown.LanguageServer;

/// <summary>
/// Represents a change an editor may offer, before it is converted to the protocol's code
/// action. A change has a title, belongs in one menu and makes a set of edits.
/// </summary>
/// <param name="Title">The text the client shows when it offers the change.</param>
/// <param name="Kind">Which menu it belongs in, one of <see cref="CodeActionKinds"/>.</param>
/// <param name="Edits">The edits it makes, in any order and across any files.</param>
/// <param name="For">The diagnostic it fixes, or null for a change not tied to any diagnostic.</param>
/// <param name="Preferred">
/// Whether a client may apply it without asking the programmer to choose: false where it is one
/// of several plausible readings of the same line, since only the programmer knows which was
/// meant.
/// </param>
/// <param name="Names">
/// The placeholder name it inserts for the programmer to replace, or null for a change that
/// inserts none.
/// </param>
/// <param name="Renames">
/// The span of the existing name for the programmer to replace, used by a change that makes no
/// edits and exists only to prompt a rename. The editor puts the caret there and starts a
/// rename.
/// </param>
/// <param name="Refused">
/// Why it cannot be applied here, or null if it can. A change that would alter what the line
/// means is offered greyed out with the reason rather than left out, so that a programmer
/// looking for it finds the reason.
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
    /// Represents a placeholder name that a change must insert even though only the programmer
    /// knows what it should be. It records which edit's text holds the name and where in that
    /// text the name starts. That offset is converted to a position in the file as it will be
    /// after the change, so that the editor can put the caret on the name and start a rename.
    /// </summary>
    /// <param name="In">The edit whose text holds the name.</param>
    /// <param name="At">Where the name starts in that edit's text.</param>
    internal sealed record Placeholder(Edit In, int At);
}
