using System.Text;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Provides the refactorings that lay out a whole expression across lines, or join it back onto
/// one. The formatter keeps the line breaks a file has and adds none, so where a long expression
/// breaks is the programmer's choice, and these make it in one step.
/// <para>
/// Laying out breaks every outermost call's arguments and every outermost set in the expression,
/// one item to a line. A call or set inside those is broken too where it does not fit on its line
/// within the line length, so a long expression gets as many levels as it needs. A
/// <c>.switch</c> keeps its value beside its opening bracket and each set beside its result. A
/// line is indented one step past the line its innermost open bracket opened on, as the
/// formatter lays it out.
/// </para>
/// <para>
/// Only an expression's brackets may hold a line break, so the brackets laid out are those of a
/// call's arguments and of a set; a macro call's arguments are not an expression's. Neither
/// refactoring is offered where the expression holds a comment, which it would have to drop.
/// </para>
/// </summary>
internal static class LineBreaks
{
    /// <summary>The line length that the layout fits lines within, unless the editor's settings give another.</summary>
    public const int DefaultLength = 100;

    /// <summary>
    /// Returns the changes offered where the caret is in an expression: laying the whole
    /// expression out across lines, and joining it onto one where it spans several.
    /// </summary>
    /// <param name="model">The file the caret is in.</param>
    /// <param name="caret">The caret's position in that file's text.</param>
    /// <param name="lineLength">The length the layout fits lines within, or 0 for no limit.</param>
    public static IEnumerable<Change> In(SemanticModel model, int caret, int lineLength = DefaultLength)
    {
        var tree = model.Tree;
        if (WholeAt(tree, caret) is not { } whole || !CanLayOut(whole))
            yield break;
        var source = tree.Text[whole.Span.Start..whole.Span.End];
        var laid = Laid(tree, whole, lineLength, broken: true);
        if (laid != source && laid.Contains('\n', StringComparison.Ordinal))
            yield return new Change("Lay out the expression across lines", CodeActionKinds.Rewrite, [new Edit(tree, whole.Span, laid)]);
        if (source.AsSpan().ContainsAny('\r', '\n'))
        {
            var joined = Laid(tree, whole, 0, broken: false);
            yield return new Change("Join the expression onto one line", CodeActionKinds.Rewrite, [new Edit(tree, whole.Span, joined)]);
        }
    }

    /// <summary>
    /// Returns the span of the first expression on the 0-based line <paramref name="line"/> of the
    /// file that laying out would break across lines, or null when there is none. A long line gets
    /// a suggestion over that expression, where the refactoring that lays it out is offered.
    /// </summary>
    public static TextSpan? Breakable(SyntaxTree tree, int line, int lineLength)
    {
        var start = tree.LineStarts[line];
        var end = tree.GetLineEnd(line);
        foreach (var token in tree.GetLine(line).Tokens)
        {
            if (token.Span.Start < start || token.Span.Start >= end
                || token.Kind is not (SyntaxKind.OpenParen or SyntaxKind.OpenBracket)
                || Brackets(token.Parent) is not { Items.Count: > 1 }
                || WholeAt(tree, token.Span.Start) is not { } whole
                || !CanLayOut(whole))
            {
                continue;
            }
            var source = tree.Text[whole.Span.Start..whole.Span.End];
            if (Laid(tree, whole, lineLength, broken: true) is var laid && laid != source && laid.Contains('\n', StringComparison.Ordinal))
                return whole.Span;
        }
        return null;
    }

    /// <summary>
    /// Returns the text of <paramref name="whole"/> laid out within <paramref name="lineLength"/>,
    /// or 0 for no limit. With <paramref name="broken"/>, its outermost calls and sets are broken
    /// whether or not they fit, as laying out asks; without it, only what does not fit is broken,
    /// which with no limit is nothing, as joining asks.
    /// </summary>
    private static string Laid(SyntaxTree tree, ExpressionSyntax whole, int lineLength, bool broken)
    {
        var line = tree.GetLineIndex(whole.Span.Start);
        var writer = new Writer(
            lineLength <= 0 ? int.MaxValue : lineLength,
            whole.Span.Start - tree.LineStarts[line],
            Edits.IndentOf(tree, tree.GetLine(line).LineIndex).Length);
        writer.Write([.. whole.DescendantTokens().Where(token => !token.IsMissing)], broken);
        return writer.ToString();
    }

    /// <summary>
    /// Returns the whole expression the caret is in, which is the outermost expression of its
    /// statement that holds it, or null when the caret is in none.
    /// </summary>
    private static ExpressionSyntax? WholeAt(SyntaxTree tree, int caret)
    {
        if (tree.Text.Length == 0)
            return null;
        ExpressionSyntax? whole = null;
        for (var node = tree.Root.FindToken(Math.Min(caret, tree.Text.Length - 1)).Parent;
            node is not null and not StatementSyntax;
            node = node.Parent)
        {
            if (node is ExpressionSyntax expression)
                whole = expression;
        }
        return whole;
    }

    /// <summary>
    /// Checks whether <paramref name="whole"/> has a call or a set with something to lay out, and
    /// no comment inside it that laying it out would drop.
    /// </summary>
    private static bool CanLayOut(ExpressionSyntax whole)
    {
        if (!((IEnumerable<SyntaxNode>)[whole, .. whole.DescendantNodes()]).Any(node => Brackets(node) is { Items.Count: > 1 }))
            return false;
        var tokens = whole.DescendantTokens().ToList();
        for (var i = 0; i < tokens.Count; i++)
        {
            var trivia = i == tokens.Count - 1 ? tokens[i].LeadingTrivia : tokens[i].LeadingTrivia.Concat(tokens[i].TrailingTrivia);
            if (trivia.Any(piece => piece.Kind == SyntaxKind.CommentTrivia))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Returns the brackets and the items of <paramref name="node"/>, where it is a call's
    /// arguments or a set whose brackets are both in the source, or null otherwise.
    /// </summary>
    private static Group? Brackets(SyntaxNode? node)
    {
        var found = node switch
        {
            ArgumentListSyntax { Parent: CallExpressionSyntax call } arguments => new Group(
                arguments.OpenParenToken, [.. arguments.Arguments], arguments.CloseParenToken,
                call.BuiltinKind == BuiltinKind.Switch),
            SetExpressionSyntax set => new Group(set.OpenBracketToken, [.. set.Items], set.CloseBracketToken, false),
            _ => null,
        };
        return found is { Open.IsMissing: false, Close.IsMissing: false } ? found : null;
    }

    /// <summary>
    /// Represents the brackets of a call's arguments or of a set, and the items between them.
    /// </summary>
    /// <param name="Open">The opening bracket.</param>
    /// <param name="Items">The arguments or values.</param>
    /// <param name="Close">The closing bracket.</param>
    /// <param name="IsSwitch">Whether the brackets are a <c>.switch</c>'s, whose items are its value and its arms.</param>
    private sealed record Group(SyntaxToken Open, IReadOnlyList<SyntaxNode> Items, SyntaxToken Close, bool IsSwitch);

    /// <summary>
    /// Writes an expression's tokens, fitting each call and set on its line or breaking it one item
    /// to a line. Between two tokens the source had space around, it writes one space.
    /// </summary>
    /// <param name="limit">The column no line should pass.</param>
    /// <param name="startColumn">The column the expression starts at in its first line.</param>
    /// <param name="indent">The indentation of the line the expression starts on.</param>
    private sealed class Writer(int limit, int startColumn, int indent)
    {
        private readonly StringBuilder text = new();

        // Where the line being written starts in the text, and how far it is indented. The first
        // line starts before the text, at the expression's own column.
        private int lineStart = -startColumn;
        private int lineIndent = indent;

        private int Column => text.Length - lineStart;

        public override string ToString() => text.ToString();

        /// <summary>
        /// Writes <paramref name="tokens"/>. The calls and sets among them that no other among
        /// them holds are broken where <paramref name="broken"/> says to, and any call or set is
        /// broken where it does not fit.
        /// </summary>
        public void Write(List<SyntaxToken> tokens, bool broken)
        {
            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.Kind is SyntaxKind.OpenParen or SyntaxKind.OpenBracket
                    && Brackets(token.Parent) is { } group && group.Open == token
                    && tokens.IndexOf(group.Close, i) is var close and >= 0)
                {
                    WriteGroup(group, broken);
                    i = close;
                    token = tokens[i];
                }
                else
                {
                    text.Append(token.Text);
                }
                if (i + 1 < tokens.Count && (token.TrailingTrivia.Count > 0 || tokens[i + 1].LeadingTrivia.Count > 0))
                    text.Append(' ');
            }
        }

        /// <summary>
        /// Writes one call's arguments or one set, on its line where it fits and need not be
        /// broken, and otherwise one item to a line, a step in from the line its bracket opens on.
        /// A group of one item is never broken, since that gains nothing, but what it holds is
        /// broken as it would be in its place.
        /// </summary>
        private void WriteGroup(Group group, bool broken)
        {
            var items = group.Items;
            if (items.Count < 2)
            {
                text.Append(group.Open.Text);
                if (items.Count == 1)
                    Write(TokensOf(items[0]), broken);
                text.Append(group.Close.Text);
                return;
            }
            var flat = Flat(group);
            if (!broken && Column + flat.Length <= limit)
            {
                text.Append(flat);
                return;
            }

            text.Append(group.Open.Text);
            var inner = lineIndent + Edits.Indent.Length;
            if (!group.IsSwitch)
            {
                for (var i = 0; i < items.Count; i++)
                {
                    NewLine(inner);
                    Write(TokensOf(items[i]), broken: false);
                    if (i + 1 < items.Count)
                        text.Append(',');
                }
            }
            else
            {
                // The value stays beside the bracket, each arm is a set and its result on a line,
                // and a result left over is the one for otherwise.
                Write(TokensOf(items[0]), broken: false);
                text.Append(',');
                var arms = (items.Count - 1) / 2;
                for (var arm = 0; arm < arms; arm++)
                {
                    NewLine(inner);
                    Write(TokensOf(items[1 + (2 * arm)]), broken: false);
                    text.Append(", ");
                    Write(TokensOf(items[2 + (2 * arm)]), broken: false);
                    if (3 + (2 * arm) < items.Count)
                        text.Append(',');
                }
                if ((items.Count - 1) % 2 == 1)
                {
                    NewLine(inner);
                    Write(TokensOf(items[^1]), broken: false);
                }
            }
            text.Append(group.Close.Text);
        }

        /// <summary>Returns a call's arguments or a set as it is written on one line.</summary>
        private static string Flat(Group group)
        {
            var writer = new Writer(int.MaxValue, 0, 0);
            writer.text.Append(group.Open.Text);
            for (var i = 0; i < group.Items.Count; i++)
            {
                if (i > 0)
                    writer.text.Append(", ");
                writer.Write(TokensOf(group.Items[i]), broken: false);
            }
            writer.text.Append(group.Close.Text);
            return writer.ToString();
        }

        /// <summary>Returns the tokens of a node that the source holds.</summary>
        private static List<SyntaxToken> TokensOf(SyntaxNode node) =>
            [.. node.DescendantTokens().Where(token => !token.IsMissing)];

        /// <summary>Starts a new line indented by <paramref name="indent"/>.</summary>
        private void NewLine(int indent)
        {
            text.Append('\n').Append(' ', indent);
            lineStart = text.Length - indent;
            lineIndent = indent;
        }
    }
}
