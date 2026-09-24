using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Provides the refactorings that lay out an expression's brackets across lines, or join them
/// back onto one. The formatter keeps the line breaks a file has and adds none, so where a long
/// call or set breaks is the programmer's choice, and these make it in one step.
/// <list type="bullet">
/// <item>Put each argument of a call, or each value of a set, on its own line.</item>
/// <item>Put each arm of a <c>.switch</c> on its own line, its set beside its result.</item>
/// <item>Join the brackets' contents onto one line.</item>
/// </list>
/// <para>
/// Only an expression's brackets may hold a line break, so these are offered on a call's
/// arguments and on a set, and not on a macro call's arguments. A line the refactoring breaks is
/// indented one step past the line the expression starts on, as the formatter lays it out. A
/// join is not offered where the brackets hold a comment, which it would have to drop.
/// </para>
/// </summary>
internal static class LineBreaks
{
    /// <summary>
    /// Returns the changes offered where the caret is inside a call's arguments or a set. They are
    /// for the innermost of those around the caret that has something to lay out, so that the
    /// caret on the one value of a <c>.switch</c> arm's set still offers the <c>.switch</c>.
    /// </summary>
    public static IEnumerable<Change> In(SemanticModel model, int caret)
    {
        foreach (var (open, items, close, what) in Around(model.Tree, caret))
        {
            if (For(model.Tree, open, items, close, what) is { } change)
                return [change];
        }
        return [];
    }

    /// <summary>
    /// Returns the change that lays out the contents of one pair of brackets, or null when there
    /// is nothing to lay out: one item on one line, or a comment a join would drop.
    /// </summary>
    private static Change? For(
        SyntaxTree tree, SyntaxToken open, IReadOnlyList<SyntaxNode> items, SyntaxToken close, Contents what)
    {
        var inside = new TextSpan(open.Span.End, close.Span.Start - open.Span.End);
        var pieces = items.Select(item => tree.Text[item.Span.Start..item.Span.End]).ToList();
        if (pieces.Count == 0 || pieces.Any(piece => piece.Length == 0))
            return null;

        if (!tree.Text.AsSpan(inside.Start, inside.Length).ContainsAny('\r', '\n'))
        {
            if (pieces.Count < 2)
                return null;
            var indent = Edits.IndentOf(tree, tree.GetLine(tree.GetLineIndex(open.Span.Start)).LineIndex) + Edits.Indent;
            var arms = what == Contents.Arms ? Arms(pieces) : null;
            var laid = arms is null
                ? "\n" + indent + string.Join(",\n" + indent, pieces)
                : pieces[0] + ",\n" + indent + string.Join(",\n" + indent, arms);
            var title = what switch
            {
                Contents.Arms when arms is not null => "Put each arm of the `.switch` on its own line",
                Contents.Values => "Put each value of the set on its own line",
                _ => "Put each argument on its own line",
            };
            return new Change(title, CodeActionKinds.Rewrite, [new Edit(tree, inside, laid)]);
        }

        return HoldsAComment(open, close, items)
            ? null
            : new Change("Join onto one line", CodeActionKinds.Rewrite, [new Edit(tree, inside, string.Join(", ", pieces))]);
    }

    /// <summary>
    /// Returns the arms of a <c>.switch</c> after its value, each a set and its result on one
    /// line, and a last result for otherwise on a line of its own. Returns null when there are no
    /// arms to lay out.
    /// </summary>
    private static List<string>? Arms(List<string> pieces)
    {
        if (pieces.Count < 3)
            return null;
        var arms = new List<string>();
        var i = 1;
        for (; i + 1 < pieces.Count; i += 2)
            arms.Add(pieces[i] + ", " + pieces[i + 1]);
        if (i < pieces.Count)
            arms.Add(pieces[i]);
        return arms;
    }

    /// <summary>
    /// Returns the brackets of each call's arguments and each set that holds the caret, innermost
    /// first, with their items and what the items are.
    /// </summary>
    private static IEnumerable<(SyntaxToken Open, IReadOnlyList<SyntaxNode> Items, SyntaxToken Close, Contents What)> Around(
        SyntaxTree tree, int caret)
    {
        if (tree.Text.Length == 0)
            yield break;
        for (var node = tree.Root.FindToken(Math.Min(caret, tree.Text.Length - 1)).Parent; node is not null; node = node.Parent)
        {
            var found = node switch
            {
                ArgumentListSyntax { Parent: CallExpressionSyntax call } arguments =>
                    ((SyntaxToken Open, IReadOnlyList<SyntaxNode> Items, SyntaxToken Close, Contents What)?)(
                        arguments.OpenParenToken, [.. arguments.Arguments], arguments.CloseParenToken,
                        call.BuiltinKind == BuiltinKind.Switch ? Contents.Arms : Contents.Arguments),
                SetExpressionSyntax set => (set.OpenBracketToken, [.. set.Items], set.CloseBracketToken, Contents.Values),
                _ => null,
            };
            if (found is var (open, _, close, _) && !open.IsMissing && !close.IsMissing
                && caret >= open.Span.End && caret <= close.Span.Start)
            {
                yield return found.Value;
            }
        }
    }

    /// <summary>Checks whether a comment stands between two brackets.</summary>
    private static bool HoldsAComment(SyntaxToken open, SyntaxToken close, IReadOnlyList<SyntaxNode> items) =>
        open.TrailingTrivia.Any(trivia => trivia.Kind == SyntaxKind.CommentTrivia)
        || items.SelectMany(item => item.Parent!.DescendantTokens())
            .Where(token => token.Span.Start >= open.Span.End && token.Span.End <= close.Span.Start)
            .Any(token => token.LeadingTrivia.Concat(token.TrailingTrivia).Any(trivia => trivia.Kind == SyntaxKind.CommentTrivia));

    /// <summary>Specifies what the brackets hold, which decides how they are laid out.</summary>
    private enum Contents
    {
        /// <summary>A call's arguments, one to a line.</summary>
        Arguments,

        /// <summary>A <c>.switch</c>'s arms, a set and its result to a line.</summary>
        Arms,

        /// <summary>A set's values, one to a line.</summary>
        Values,
    }
}
