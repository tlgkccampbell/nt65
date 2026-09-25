namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Represents a change the editor offers, either a fix for a diagnostic or a rewrite requested at
/// a selection.
/// </summary>
/// <param name="Title">The title the client displays for the change.</param>
/// <param name="Kind">
/// The menu the change belongs in, which is <c>quickfix</c> or one of the <c>refactor</c> kinds.
/// </param>
/// <param name="Diagnostics">
/// The diagnostic the change fixes, or none for a change that no diagnostic reported.
/// </param>
/// <param name="Edit">
/// The edits that make the change, or null when the client finds them later with
/// <c>codeAction/resolve</c>.
/// </param>
/// <param name="IsPreferred">
/// Whether a client may apply this change without asking the programmer to choose. It is left
/// null when the change is one of several plausible readings of the same line, because only the
/// programmer knows which was meant.
/// </param>
/// <param name="Command">
/// The command the client runs after applying the change, or null if the edits alone complete it.
/// A change that inserts a placeholder name, such as a routine extracted from another, asks the
/// client to start a rename on the placeholder so that the programmer can name it.
/// </param>
/// <param name="Disabled">
/// The reason the change cannot be applied here, or null if it can. A change that would alter the
/// meaning of the line is still offered, greyed out with the reason, so that a programmer looking
/// for it finds the reason rather than nothing.
/// </param>
/// <param name="Data">
/// What the server needs to find the edits of an action sent without them, or null for an action
/// sent whole.
/// </param>
internal sealed record CodeAction(
    string Title,
    string Kind,
    IReadOnlyList<Diagnostic> Diagnostics,
    WorkspaceEdit? Edit,
    bool? IsPreferred = null,
    Command? Command = null,
    CodeActionDisabled? Disabled = null,
    CodeActionData? Data = null);
