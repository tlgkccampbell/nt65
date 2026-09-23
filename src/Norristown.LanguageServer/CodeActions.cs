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
        return [.. changes
            .Where(change => Wanted(only, change.Kind)
                && (change.Edits.Count > 0 || change.Renames is not null || change.Refused is not null))
            .Select(Spelled)];
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
            change.Preferred,
            Renaming(change),
            change.Refused is { } why ? new Protocol.CodeActionDisabled(why) : null);
    }

    /// <summary>
    /// The command that starts a rename on the placeholder name a change wrote, or null for a
    /// change that wrote none. The position is in the file as it is after the change, which is
    /// what the client has by the time it runs the command.
    /// </summary>
    private static Protocol.Command? Renaming(Change change)
    {
        // A change that only prompts a rename makes no edits: the name already exists, so the
        // rename starts on it where it is.
        if (change.Renames is { } written)
        {
            return new Protocol.Command(
                "Rename", "nt65.rename",
                [Lsp.ToUri(written.File), written.Line - 1, written.StartColumn - 1]);
        }
        if (change.Names is not { } placeholder)
            return null;
        var tree = placeholder.In.Tree;
        var edits = change.Edits.Where(edit => edit.Tree == tree).ToList();

        // Where the name ends up: the start of the edit that writes it, shifted by the net
        // length change of every earlier edit in the file, plus the name's offset in that
        // edit's text.
        var at = placeholder.In.Span.Start + placeholder.At;
        foreach (var edit in edits.Where(edit => edit.Span.Start < placeholder.In.Span.Start))
            at += edit.Text.Length - edit.Span.Length;

        var text = tree.Text;
        foreach (var edit in edits.OrderByDescending(edit => edit.Span.Start))
            text = text[..edit.Span.Start] + edit.Text + text[edit.Span.End..];

        var line = text[..at].Count(c => c == '\n');
        var character = at - (text[..at].LastIndexOf('\n') + 1);
        return new Protocol.Command("Rename", "nt65.rename", [Lsp.ToUri(tree.Path), line, character]);
    }
}
