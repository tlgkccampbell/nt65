namespace Norristown.LanguageServer.Protocol;

/// <summary>A change an editor offers: a fix for a diagnostic, or a rewrite asked for at a selection.</summary>
/// <param name="Title">What the client offers it as.</param>
/// <param name="Kind">Which menu it belongs in: <c>quickfix</c>, or one of the <c>refactor</c> kinds.</param>
/// <param name="Diagnostics">The diagnostic it fixes, or none for a change nothing reported.</param>
/// <param name="Edit">The change.</param>
/// <param name="IsPreferred">
/// Whether it is the change to apply without asking which, left unsaid where it is one of
/// several readings of the same line and the programmer is the one who knows.
/// </param>
/// <param name="Command">
/// What the client runs once it has applied the change, or null for a change that is done when
/// it is written: a routine lifted out of another is named by the programmer, so the client is
/// asked to start a rename on the name it was given to begin with.
/// </param>
internal sealed record CodeAction(
    string Title,
    string Kind,
    IReadOnlyList<Diagnostic> Diagnostics,
    WorkspaceEdit Edit,
    bool? IsPreferred = null,
    Command? Command = null);
