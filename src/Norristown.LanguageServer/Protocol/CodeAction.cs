namespace Norristown.LanguageServer.Protocol;

/// <summary>A change an editor offers: a fix for a diagnostic, or a rewrite asked for at a selection.</summary>
/// <param name="Title">What the client offers it as.</param>
/// <param name="Kind">Which menu it belongs in: <c>quickfix</c>, or one of the <c>refactor</c> kinds.</param>
/// <param name="Diagnostics">The diagnostic it fixes, or none for a change nothing reported.</param>
/// <param name="Edit">The change.</param>
/// <param name="IsPreferred">
/// Whether a client may apply this change without asking the programmer to choose. Left null
/// where the change is one of several plausible readings of the same line, since only the
/// programmer knows which was meant.
/// </param>
/// <param name="Command">
/// What the client runs once it has applied the change, or null for a change that is complete
/// once its edits are applied. A change that writes a placeholder name, such as a routine
/// extracted from another, asks the client to start a rename on that placeholder so the
/// programmer can name it.
/// </param>
/// <param name="Disabled">
/// Why the change cannot be applied here, or null if it can. A change that would alter what the
/// line means is still offered, greyed out with the reason, so that a programmer looking for
/// it finds the reason rather than nothing.
/// </param>
internal sealed record CodeAction(
    string Title,
    string Kind,
    IReadOnlyList<Diagnostic> Diagnostics,
    WorkspaceEdit Edit,
    bool? IsPreferred = null,
    Command? Command = null,
    CodeActionDisabled? Disabled = null);
