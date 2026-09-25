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
/// <param name="Later">
/// Finds the edits when they are wanted, for a change whose edits cost too much to find each time
/// the caret moves, such as a rename across the program. <paramref name="Edits"/> is empty when
/// this is given.
/// </param>
internal sealed record Change(
    string Title,
    string Kind,
    IReadOnlyList<Edit> Edits,
    Diagnostic? For = null,
    bool? Preferred = null,
    Change.Placeholder? Names = null,
    Span? Renames = null,
    string? Refused = null,
    Func<IReadOnlyList<Edit>>? Later = null)
{
    /// <summary>
    /// Returns a change whose edits are found only when they are wanted, as <paramref name="later"/>
    /// finds them.
    /// </summary>
    public static Change Deferred(
        string title, string kind, Func<IReadOnlyList<Edit>> later, Diagnostic? diagnostic = null, bool? preferred = null) =>
        new(title, kind, [], diagnostic, preferred, Later: later);

    /// <summary>
    /// Returns the change with its edits found, which is the change itself unless they were left
    /// for later.
    /// </summary>
    public Change Made() => Later is { } later ? this with { Edits = later(), Later = null } : this;

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
