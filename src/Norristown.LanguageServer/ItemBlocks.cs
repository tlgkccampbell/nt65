using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Provides the refactorings that write a list directive's items in an item block, or write a
/// block's items back on the directive's line. <c>.export</c>, <c>.import</c>, <c>.next</c> and
/// <c>.use</c> take either form, as <see cref="SyntaxFacts.IsItemBlock"/> describes, and a
/// <c>.use</c> writes its items on one line in braces.
/// <para>
/// A block gets one item to a line, as <see cref="LineBreaks"/> gives a call one argument to a
/// line. That leaves room for a comment beside each item, and the layout does not depend on the
/// line length. A comment after the directive's line moves to the line that opens the block, and
/// back again. Writing a block on one line is not offered where a comment on any other line of
/// the block would be lost.
/// </para>
/// </summary>
internal static class ItemBlocks
{
    /// <summary>
    /// Returns the change offered where the caret is on a list directive's line or in the item
    /// block it opens. That is writing the items as a block where they are on one line, and
    /// writing them on one line where they are in a block.
    /// </summary>
    /// <param name="model">The file the caret is in.</param>
    /// <param name="caretLine">The 0-based line the caret is on.</param>
    public static IEnumerable<Change> In(SemanticModel model, int caretLine)
    {
        var tree = model.Tree;
        if (ListOn(tree, caretLine) is not { } list)
            yield break;
        if (list.Block is null && AsBlock(tree, list) is { } laid)
            yield return new Change("Write the items as a block", CodeActionKinds.Rewrite, [laid]);
        else if (list.Block is not null && OnOneLine(tree, list) is { } joined)
            yield return new Change("Write the items on one line", CodeActionKinds.Rewrite, [joined]);
    }

    /// <summary>
    /// Returns the span of the list directive on the 0-based line <paramref name="line"/> whose
    /// items run past <paramref name="lineLength"/> and could be written as a block, or null when
    /// there is none. A long line gets a suggestion over that directive, where the refactoring
    /// that writes the block is offered.
    /// </summary>
    public static TextSpan? Breakable(SyntaxTree tree, int line, int lineLength)
    {
        if (ListOn(tree, line) is not { Block: null, Inline.Count: > 1 } list
            || tree.GetLineIndex(list.Directive.Span.Start) != line
            || list.End - tree.LineStarts[line] <= lineLength
            || AsBlock(tree, list) is null)
        {
            return null;
        }
        return list.Directive.Span;
    }

    /// <summary>
    /// Returns the list directive on the 0-based line <paramref name="line"/>, or the one whose
    /// item block holds that line, or null when there is neither.
    /// </summary>
    private static ItemList? ListOn(SyntaxTree tree, int line)
    {
        if (line < 0 || line >= tree.LineCount)
            return null;
        var syntax = tree.GetLine(line);
        var statement = syntax.Parent is BlockSyntax block && SyntaxFacts.IsItemBlock(block.BlockKind)
            ? block.Opener.Statement
            : syntax.Statement;
        return statement switch
        {
            ExportDirectiveSyntax export => new(export, [.. export.InlineItems], [.. export.Items], export.OpenBraceToken, null, export.ItemBlock),
            ImportDirectiveSyntax import => new(import, [.. import.InlineItems], [.. import.Items], import.OpenBraceToken, null, import.ItemBlock),
            NextDirectiveSyntax { QuestionToken: null, ReturnToken: null } next =>
                new(next, [.. next.InlineTargets], [.. next.Targets], next.OpenBraceToken, null, next.ItemBlock),
            UseDirectiveSyntax { OpenBraceToken: not null } use =>
                new(use, [.. use.InlineItems], [.. use.Items], use.OpenBraceToken, use.CloseBraceToken, use.ItemBlock),
            _ => null,
        };
    }

    /// <summary>
    /// Returns the edit that writes the items on the directive's line as a block, one to a line,
    /// or null where there are fewer than two, the line has an error, or a comment among the
    /// items would be lost.
    /// </summary>
    private static Edit? AsBlock(SyntaxTree tree, ItemList list)
    {
        var line = tree.GetLineIndex(list.Directive.Span.Start);
        if (list.Inline.Count < 2 || tree.GetLine(line).ContainsDiagnostics || HasComment(list.Inline))
            return null;

        // The braces of `.use a::{b, c}` become the block's, and the other directives' items
        // give way to a `{`.
        var start = list.CloseBrace is null ? list.Inline[0].Span.Start : list.OpenBrace!.Value.Span.Start;
        var last = tree.GetLineIndex(list.End);
        var indent = Edits.IndentOf(tree, line);
        var lines = list.Inline.Select(item => $"{indent}{Edits.Indent}{item.GetTextOnOneLine()}\n");
        var text = $"{{{Remark(tree, list.End, last)}\n{string.Concat(lines)}{indent}}}";
        return new Edit(tree, new TextSpan(start, Lines.EndOf(tree, last) - start), text);
    }

    /// <summary>
    /// Returns the edit that writes the items of the directive's block on its line, or null where
    /// the block has an error, is not closed by a <c>}</c> line, or holds a comment that would be
    /// lost. A comment on the line that opens the block stays on the line.
    /// </summary>
    private static Edit? OnOneLine(SyntaxTree tree, ItemList list)
    {
        if (list.Block is not { HasCloser: true, ContainsDiagnostics: false } block
            || list.OpenBrace is not { } brace
            || list.Inline.Count > 0
            || list.Items.Count == 0
            || block.Members.Skip(1).Any(member => member.DescendantTokens().Any(HasComment)))
        {
            return null;
        }
        var items = string.Join(", ", list.Items.Select(item => item.GetTextOnOneLine()));
        var text = list.Directive is UseDirectiveSyntax ? $"{{{items}}}" : items;
        var opener = tree.GetLineIndex(brace.Span.Start);
        var end = Lines.EndOf(tree, block.Closer!.LineIndex);
        return new Edit(tree, new TextSpan(brace.Span.Start, end - brace.Span.Start), text + Remark(tree, brace.Span.End, opener));
    }

    /// <summary>
    /// Checks whether a comment is among <paramref name="items"/>, which writing them elsewhere
    /// would lose. A comment after the last of them ends the line, and stays with it.
    /// </summary>
    private static bool HasComment(IReadOnlyList<SyntaxNode> items)
    {
        var tokens = items.SelectMany(item => item.DescendantTokens()).ToList();
        return tokens.Count > 0 && (tokens[..^1].Any(HasComment) || tokens[^1].LeadingTrivia.Any(IsComment));
    }

    /// <summary>Checks whether a comment is in the trivia on either side of <paramref name="token"/>.</summary>
    private static bool HasComment(SyntaxToken token) => token.LeadingTrivia.Concat(token.TrailingTrivia).Any(IsComment);

    /// <summary>Checks whether <paramref name="trivia"/> is a comment.</summary>
    private static bool IsComment(SyntaxTrivia trivia) => trivia.Kind == SyntaxKind.CommentTrivia;

    /// <summary>
    /// Returns what follows <paramref name="from"/> on the 0-based line <paramref name="line"/>,
    /// which is a comment and the space before it, or an empty string where only space follows.
    /// </summary>
    private static string Remark(SyntaxTree tree, int from, int line)
    {
        var rest = tree.Text[from..Lines.EndOf(tree, line)];
        return string.IsNullOrWhiteSpace(rest) ? "" : rest;
    }

    /// <summary>
    /// Represents a list directive, with its items and the brackets that hold them.
    /// </summary>
    /// <param name="Directive">The directive.</param>
    /// <param name="Inline">The items on the directive's own line.</param>
    /// <param name="Items">Every item, those on the directive's line and those in its block.</param>
    /// <param name="OpenBrace">The <c>{</c> that opens the block or a <c>.use</c>'s braces, or null.</param>
    /// <param name="CloseBrace">The <c>}</c> that closes a <c>.use</c>'s braces on its line, or null.</param>
    /// <param name="Block">The item block the directive opens, or null.</param>
    private sealed record ItemList(
        StatementSyntax Directive,
        IReadOnlyList<SyntaxNode> Inline,
        IReadOnlyList<SyntaxNode> Items,
        SyntaxToken? OpenBrace,
        SyntaxToken? CloseBrace,
        BlockSyntax? Block)
    {
        /// <summary>Gets where the items on the directive's line end, after a <c>.use</c>'s <c>}</c>.</summary>
        public int End => CloseBrace?.Span.End ?? Inline[^1].Span.End;
    }
}
