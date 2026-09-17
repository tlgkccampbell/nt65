namespace Norristown.LanguageServer.Protocol;

/// <summary>A change that fixes a diagnostic.</summary>
/// <param name="Title">What the client offers it as.</param>
/// <param name="Kind">Always <c>quickfix</c>.</param>
/// <param name="Diagnostics">The diagnostic it fixes.</param>
/// <param name="Edit">The change.</param>
/// <param name="IsPreferred">Whether it is the fix to apply without asking which.</param>
internal sealed record CodeAction(
    string Title, string Kind, IReadOnlyList<Diagnostic> Diagnostics, WorkspaceEdit Edit, bool IsPreferred);
