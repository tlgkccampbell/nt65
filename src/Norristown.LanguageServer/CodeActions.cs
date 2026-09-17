using Norristown.Semantics;

namespace Norristown.LanguageServer;

/// <summary>
/// What an editor offers at a place in a file: the fixes the diagnostics there name, and the
/// rewrites a selection allows. Every edit is worked out against the files as they are now,
/// and a client that asked for one kind of change is given only that kind.
/// </summary>
internal static class CodeActions
{
    /// <summary>
    /// The changes offered over <paramref name="range"/> of <paramref name="model"/>'s file.
    /// <paramref name="only"/> is the kinds the client will show, or null for all of them.
    /// </summary>
    public static IReadOnlyList<Protocol.CodeAction> In(
        ProgramAnalysis analysis, SemanticModel model, Protocol.Range range, IReadOnlyList<string>? only = null)
    {
        var changes = new List<Change>();
        if (Wanted(only, CodeActionKinds.QuickFix))
            changes.AddRange(Fixes.In(analysis, model, range));
        if (Wanted(only, CodeActionKinds.Rewrite) || Wanted(only, CodeActionKinds.Extract))
            changes.AddRange(Refactors.In(analysis, model, range));
        return [.. changes.Where(change => Wanted(only, change.Kind) && change.Edits.Count > 0).Select(Spelled)];
    }

    /// <summary>
    /// Whether the client asked for changes of <paramref name="kind"/>: it asked for every kind,
    /// for this one, or for a kind this one is part of, as <c>refactor</c> is of
    /// <c>refactor.rewrite</c>.
    /// </summary>
    private static bool Wanted(IReadOnlyList<string>? only, string kind) =>
        only is null || only.Count == 0
            || only.Any(asked => kind == asked || kind.StartsWith(asked + ".", StringComparison.Ordinal));

    /// <summary>A change as the protocol spells it: its edits grouped by the file each belongs to.</summary>
    private static Protocol.CodeAction Spelled(Change change)
    {
        var edits = change.Edits
            .GroupBy(edit => edit.Tree)
            .ToDictionary(
                group => Lsp.ToUri(group.Key.Path),
                group => (IReadOnlyList<Protocol.TextEdit>)[.. group
                    .OrderBy(edit => edit.Span.Start)
                    .Select(edit => new Protocol.TextEdit(Lsp.ToRange(edit.Tree, edit.Span), edit.Text))]);
        return new Protocol.CodeAction(
            change.Title,
            change.Kind,
            change.For is { } diagnostic ? [Lsp.ToDiagnostic(diagnostic)] : [],
            new Protocol.WorkspaceEdit(edits),
            change.Preferred);
    }
}
