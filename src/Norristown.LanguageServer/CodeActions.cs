using Norristown.Semantics;

namespace Norristown.LanguageServer;

/// <summary>
/// Provides the code actions an editor offers at a place in a file, which are the fixes for the
/// diagnostics there and the rewrites a selection allows. Every edit is computed against the files
/// as they are now, and a client that asked for one kind of change is given only that kind.
/// </summary>
internal static class CodeActions
{
    /// <summary>
    /// Returns the changes offered over <paramref name="range"/> of <paramref name="model"/>'s
    /// file. <paramref name="only"/> lists the kinds the client will show, or is null for all of
    /// them, and <paramref name="lineLength"/> is the length a layout fits lines within.
    /// </summary>
    public static IReadOnlyList<Protocol.CodeAction> In(
        ProgramAnalysis analysis, SemanticModel model, Protocol.Range range, IReadOnlyList<string>? only = null,
        int lineLength = LineBreaks.DefaultLength) =>
        [.. Changes(analysis, model, range, only, lineLength).Select(change => ToCodeAction(change.Made()))];

    /// <summary>
    /// Returns the changes offered over <paramref name="range"/> of the file at
    /// <paramref name="uri"/>, as <see cref="In"/> does, but with each change's edits left for
    /// <see cref="Resolved"/> to find. Finding edits can mean following a name through the whole
    /// program, and the client asks at every caret move but applies at most one change.
    /// </summary>
    /// <remarks>
    /// A change whose command needs its edits, such as a rename of the placeholder it inserts, is
    /// returned whole.
    /// </remarks>
    public static IReadOnlyList<Protocol.CodeAction> Unresolved(
        ProgramAnalysis analysis, SemanticModel model, string uri, Protocol.Range range, IReadOnlyList<string>? only,
        int lineLength)
    {
        var changes = Changes(analysis, model, range, only, lineLength);
        return
        [
            .. changes.Select((change, index) => change.Names is null && change.Renames is null
                ? ToCodeAction(change with { Edits = [], Later = null }) with
                {
                    Edit = null,
                    Data = new Protocol.CodeActionData(uri, range, only, index),
                }
                : ToCodeAction(change.Made())),
        ];
    }

    /// <summary>
    /// Returns <paramref name="action"/>, sent by <see cref="Unresolved"/>, with its edits.
    /// Returns null when the change is no longer offered where it was, because the file has
    /// changed since.
    /// </summary>
    public static Protocol.CodeAction? Resolved(
        ProgramAnalysis analysis, SemanticModel model, Protocol.CodeAction action, int lineLength)
    {
        if (action.Data is not { } data)
            return action;
        var changes = Changes(analysis, model, data.Range, data.Only, lineLength);
        bool Same(Change change) => change.Title == action.Title && change.Kind == action.Kind;
        var found = data.Index < changes.Count && Same(changes[data.Index])
            ? changes[data.Index]
            : changes.FirstOrDefault(Same);
        return found is null ? null : ToCodeAction(found.Made());
    }

    /// <summary>
    /// Returns the changes offered over <paramref name="range"/>, of the kinds the client asked
    /// for, before any edits left for later are found.
    /// </summary>
    private static List<Change> Changes(
        ProgramAnalysis analysis, SemanticModel model, Protocol.Range range, IReadOnlyList<string>? only, int lineLength)
    {
        var changes = new List<Change>();
        if (Wanted(only, CodeActionKinds.QuickFix))
            changes.AddRange(Fixes.In(analysis, model, range));
        if (Wanted(only, CodeActionKinds.Rewrite) || Wanted(only, CodeActionKinds.Extract))
            changes.AddRange(Refactors.In(analysis, model, range, lineLength));
        return [.. changes
            .Where(change => Wanted(only, change.Kind)
                && (change.Edits.Count > 0 || change.Later is not null || change.Renames is not null
                    || change.Refused is not null))];
    }

    /// <summary>
    /// Checks whether the client asked for changes of <paramref name="kind"/>. It did if it asked
    /// for every kind, for this kind, or for a kind this one is part of, as <c>refactor</c> is of
    /// <c>refactor.rewrite</c>.
    /// </summary>
    private static bool Wanted(IReadOnlyList<string>? only, string kind) =>
        only is null || only.Count == 0
            || only.Any(asked => kind == asked || kind.StartsWith(asked + ".", StringComparison.Ordinal));

    /// <summary>
    /// Converts a change to the protocol's code action, with its edits grouped by the file each
    /// belongs to.
    /// </summary>
    private static Protocol.CodeAction ToCodeAction(Change change)
    {
        change = WithLineBreaksOfFiles(change);
        var edits = change.Edits
            .GroupBy(edit => edit.Tree)
            .ToDictionary(
                group => Uris.ToUri(group.Key.Path),
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
    /// Returns <paramref name="change"/> with the line breaks in its edits written the way each
    /// edit's file writes them, as <see cref="Edits.WithLineBreaksOf"/> does. The placeholder's
    /// offset moves with its text, so that the rename starts where the client will find the name.
    /// </summary>
    private static Change WithLineBreaksOfFiles(Change change)
    {
        static Edit Converted(Edit edit) => edit with { Text = Edits.WithLineBreaksOf(edit.Tree, edit.Text) };
        return change with
        {
            Edits = [.. change.Edits.Select(Converted)],
            Names = change.Names is { In: var edit, At: var at }
                ? new Change.Placeholder(Converted(edit), Edits.WithLineBreaksOf(edit.Tree, edit.Text[..at]).Length)
                : null,
        };
    }

    /// <summary>
    /// Returns the command that starts a rename on the placeholder name a change inserted, or
    /// null for a change that inserted none. The position is in the file as it is after the
    /// change, which is the text the client has by the time it runs the command.
    /// </summary>
    private static Protocol.Command? Renaming(Change change)
    {
        // A change that only prompts a rename makes no edits: the name already exists, so the
        // rename starts on it where it is.
        if (change.Renames is { } existing)
        {
            var start = Lsp.ToRange(existing).Start;
            return new Protocol.Command("Rename", "nt65.rename", [Uris.ToUri(existing.File), start.Line, start.Character]);
        }
        if (change.Names is not { } placeholder)
            return null;
        var tree = placeholder.In.Tree;
        var edits = change.Edits.Where(edit => edit.Tree == tree).ToList();

        // The name ends up at the start of the edit that inserts it, shifted by the net length
        // change of every earlier edit in the file, plus the name's offset in that edit's text.
        var at = placeholder.In.Span.Start + placeholder.At;
        foreach (var edit in edits.Where(edit => edit.Span.Start < placeholder.In.Span.Start))
            at += edit.Text.Length - edit.Span.Length;

        var text = tree.Text;
        foreach (var edit in edits.OrderByDescending(edit => edit.Span.Start))
            text = text[..edit.Span.Start] + edit.Text + text[edit.Span.End..];

        var named = Lsp.ToPosition(text, at);
        return new Protocol.Command("Rename", "nt65.rename", [Uris.ToUri(tree.Path), named.Line, named.Character]);
    }
}
