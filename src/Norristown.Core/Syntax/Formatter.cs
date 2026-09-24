using System.Text;

namespace Norristown.Syntax;

/// <summary>
/// Formats an nt65 file into its single canonical layout, which has no options. Whitespace
/// between tokens has no meaning in the language. So re-indenting a line, trimming the text after
/// its last token, and changing the gaps a run of lines lines up on cannot change what any line
/// means.
/// <list type="bullet">
/// <item><description>
/// Labels are indented like any other line, at the margin of the block or file that holds them.
/// </description></item>
/// <item><description>
/// The lines inside a block are indented one step further than the line that opens it.
/// </description></item>
/// <item><description>
/// A routine's cheap locals are pulled back to the routine's own margin, so they read as the
/// routine's structure rather than as another instruction.
/// </description></item>
/// <item><description>
/// A <see cref="Run"/> of lines of one kind is lined up: named data lines so that their
/// directives start in one column, and <c>.const</c> lines and a record initializer's
/// <c>name = value</c> lines so that their values start in one column. A run's trailing comments
/// start in one column too.
/// </description></item>
/// <item><description>
/// A line that continues an expression from the line before it is indented one step further
/// than the line its innermost open bracket opened on, so an expression nested across lines gets
/// a level for each line that leaves a bracket open. A line that starts with a closing bracket
/// goes back to the margin of the line that bracket opened on.
/// </description></item>
/// </list>
/// The generated ca65 already uses this layout.
/// </summary>
public static class Formatter
{
    /// <summary>The number of spaces by which a block indents its contents.</summary>
    public const int IndentWidth = 4;

    /// <summary>
    /// The column, counted from 0, at which a run's trailing comments start unless a line of it is
    /// too long. It is the column the generated ca65 puts its comments in.
    /// </summary>
    private const int CommentColumn = 36;

    /// <summary>The whitespace characters trimmed from both ends of a line before it is laid out.</summary>
    private static readonly char[] Blank = [' ', '\t'];

    /// <summary>
    /// Specifies which kind of run a line belongs to. A run is lines of one kind with nothing
    /// between them but lines that hold only a comment, and each kind lines up on its own gap.
    /// </summary>
    private enum Run
    {
        /// <summary>A line that belongs to no run.</summary>
        None,

        /// <summary>
        /// A named data line, or a member of a layout, which lines up on its directive. Data found
        /// elsewhere with no element type is in the run too, but has no directive to line up.
        /// </summary>
        Data,

        /// <summary>A <c>.const</c> line, which lines up on its <c>=</c> or <c>?=</c>.</summary>
        Const,

        /// <summary>A record initializer's <c>name = value</c> line, which lines up on its <c>=</c>.</summary>
        Member,
    }

    /// <summary>Returns the text of <paramref name="tree"/>, formatted.</summary>
    public static string Format(SyntaxTree tree)
    {
        var changes = Changes(tree, 0, tree.LineCount - 1);
        if (changes.Count == 0)
            return tree.Text;

        var text = tree.Text;
        var built = new StringBuilder(text.Length);
        var at = 0;
        foreach (var change in changes)
        {
            built.Append(text, at, change.Start - at).Append(change.NewText);
            at = change.Start + change.Length;
        }
        return built.Append(text, at, text.Length - at).ToString();
    }

    /// <summary>
    /// Returns the changes that formatting lines <paramref name="first"/> to
    /// <paramref name="last"/> would make, with line indexes 0-based and inclusive. There is one
    /// replacement per changed line, in order. The whole file is laid out either way, because a
    /// line's indentation depends on the blocks around it, and a line's alignment depends on the
    /// rest of its run. Only changes to the requested lines are returned.
    /// </summary>
    public static IReadOnlyList<TextChange> Changes(SyntaxTree tree, int first, int last)
    {
        var formatted = Formatted(tree);
        var changes = new List<TextChange>();
        for (var i = Math.Max(first, 0); i <= Math.Min(last, tree.LineCount - 1); i++)
        {
            var start = tree.LineStarts[i];
            var length = Width(tree, i);
            if (!tree.Text.AsSpan(start, length).SequenceEqual(formatted[i]))
                changes.Add(new TextChange(start, length, formatted[i]));
        }
        return changes;
    }

    /// <summary>
    /// Returns every line of <paramref name="tree"/> as the formatter outputs it, without its line
    /// break.
    /// </summary>
    private static string[] Formatted(SyntaxTree tree)
    {
        var depths = new int[tree.LineCount];
        Walk(tree.Root, 0, depths);

        var continued = new Dictionary<int, int>();
        for (var i = 0; i < tree.LineCount; i++)
        {
            var line = tree.GetLine(i);
            if (line.LineIndex == i && line.LastLineIndex > i)
                Continued(tree, line, Math.Max(depths[i] - (IsCheapLocal(line) ? 1 : 0), 0) * IndentWidth, continued);
        }

        var pieces = new Piece[tree.LineCount];
        for (var i = 0; i < tree.LineCount; i++)
        {
            var line = tree.GetLine(i);
            var content = tree.Text.AsSpan(tree.LineStarts[i], Width(tree, i)).TrimEnd(Blank);
            if (content.TrimStart(Blank).IsEmpty)
            {
                pieces[i] = new Piece(0, Run.None, "", null, "", null, false, false);
                continue;
            }

            // A line that continues an expression is indented by the brackets still open at its
            // start, which were worked out with the line the expression starts on.
            if (line.LineIndex != i)
            {
                var indent = continued.GetValueOrDefault(i, (depths[line.LineIndex] + 1) * IndentWidth);
                pieces[i] = new Piece(indent, Run.None, content.TrimStart(Blank).ToString(), null, "", null, false, false);
                continue;
            }

            // A cheap local belongs to the routine around it, not to the run of instructions it
            // points into the middle of, so it goes at the routine's own margin.
            var margin = Math.Max(depths[i] - (IsCheapLocal(line) ? 1 : 0), 0) * IndentWidth;
            pieces[i] = Split(tree, line, i, margin, content);
        }
        return Assembled(pieces);
    }

    /// <summary>
    /// Splits the first line of <paramref name="line"/> into the piece it is. A line of a run is
    /// split at the gap it lines up on and before its trailing comment, and any other line is kept
    /// as one piece after its indent.
    /// </summary>
    private static Piece Split(SyntaxTree tree, LineSyntax line, int index, int indent, ReadOnlySpan<char> content)
    {
        var start = tree.LineStarts[index];
        var comment = CommentStart(tree, line, index) is { } at ? at - start : -1;
        var code = (comment < 0 ? content : content[..comment]).TrimEnd(Blank);
        var note = comment < 0 ? null : content[comment..].ToString();

        var colon = NamedData(line);
        if (colon >= 0 && TextOffset(line, colon + 1) < code.Length)
        {
            var name = code[TextOffset(line, 0)..(TextOffset(line, colon) + 1)].ToString();
            return new Piece(indent, Run.Data, name, null, code[TextOffset(line, colon + 1)..].ToString(), note, false, false);
        }

        // Data found elsewhere with no element type has no directive to line up, but it is data
        // like the lines around it, so it stays in their run rather than ending it.
        if (line.Statement is DataDeclarationSyntax { Directive: null, Address: not null })
            return new Piece(indent, Run.Data, code[TextOffset(line, 0)..].ToString(), null, "", note, false, true);

        // A `.const` line lines up from its first token, which may be `.export`, and a member line
        // from its name. Either one lines up on its `=` only when the value starts on the line.
        var (run, head, equals) = line.Statement switch
        {
            ConstantDeclarationSyntax { Keyword.IsMissing: false, Name.IsMissing: false, EqualsToken: { IsMissing: false } op }
                when op.Span.End - start <= code.Length => (Run.Const, line.Tokens[0], op),
            MemberValueSyntax { EqualsToken: { IsMissing: false } op } member
                when line.Parent is BlockSyntax { BlockKind: BlockKind.RecordInitializer } && op.Span.End - start <= code.Length
                => (Run.Member, member.Name, op),
            _ => (Run.None, default, default),
        };
        if (run == Run.None)
            return new Piece(indent, Run.None, content.TrimStart(Blank).ToString(), null, "", null, line.LineKind == LineKind.Blank, false);

        var before = code[(head.Span.Start - start)..(equals.Span.Start - start)].TrimEnd(Blank).ToString();
        var after = code[(equals.Span.End - start)..].TrimStart(Blank).ToString();
        return new Piece(indent, run, before, equals.Text, after, note, false, false);
    }

    /// <summary>
    /// Assembles the formatted lines. Each run is lined up on the column its widest line needs,
    /// so that a run reads as a column, as in hand-written ca65. A comment-only line inside a run
    /// does not end it, but any other line does, including an empty one and a line of another
    /// kind of run.
    /// </summary>
    private static string[] Assembled(Piece[] pieces)
    {
        var formatted = new string[pieces.Length];
        for (var i = 0; i < pieces.Length; i++)
        {
            var piece = pieces[i];
            if (piece.Run == Run.None)
            {
                formatted[i] = piece.Head.Length == 0 ? "" : new string(' ', piece.Indent) + piece.Head;
                continue;
            }

            var end = i;
            for (var next = i + 1; next < pieces.Length; next++)
            {
                if (pieces[next].Run == piece.Run)
                    end = next;
                else if (!pieces[next].OnlyComment)
                    break;
            }
            var members = Enumerable.Range(i, end - i + 1).Where(line => pieces[line].Run == piece.Run).ToList();

            // A data line's directive starts one column past its longest name. A `.const` or
            // member line's `=` is one column past its longest name, and a `?=` puts its `?` in
            // the column before, so that every value starts in one column.
            var column = members.Where(line => !pieces[line].Unaligned).DefaultIfEmpty(i).Max(line => pieces[line].Indent
                + pieces[line].Head.Length + (pieces[line].Operator is { } op ? op.Length : 0)) + 1;
            var code = new Dictionary<int, string>();
            foreach (var line in members)
            {
                var part = pieces[line];
                var head = new string(' ', part.Indent) + part.Head;
                code[line] = part.Unaligned ? head : part.Operator is not { } op
                    ? head + new string(' ', column - head.Length) + part.Tail
                    : head + new string(' ', column - op.Length - head.Length) + op + (part.Tail.Length == 0 ? "" : " " + part.Tail);
            }

            // The run's trailing comments start in one column: the one the generated ca65 uses,
            // or two past the longest line that has one.
            var commented = members.Where(line => pieces[line].Comment is not null).ToList();
            var at = commented.Count == 0 ? 0 : Math.Max(CommentColumn, commented.Max(line => code[line].Length + 2));
            for (var line = i; line <= end; line++)
            {
                if (!code.TryGetValue(line, out var text))
                {
                    formatted[line] = pieces[line].Head.Length == 0 ? "" : new string(' ', pieces[line].Indent) + pieces[line].Head;
                    continue;
                }
                formatted[line] = pieces[line].Comment is { } note ? text + new string(' ', at - text.Length) + note : text;
            }
            i = end;
        }
        return formatted;
    }

    /// <summary>
    /// Records in <paramref name="indents"/> how far each line after the first of a joined line is
    /// indented, given <paramref name="first"/>, the first line's. A line is one step in from the
    /// line its innermost open bracket opened on, and a line that starts by closing a bracket is
    /// at the margin of the line that bracket opened on. A line holding only a comment is indented
    /// as the next line of code would be at that point.
    /// </summary>
    private static void Continued(SyntaxTree tree, LineSyntax line, int first, Dictionary<int, int> indents)
    {
        var opened = new Stack<int>();
        var margins = new Dictionary<int, int> { [line.LineIndex] = first };
        var current = line.LineIndex;
        int Inside() => margins[opened.Count > 0 ? opened.Peek() : line.LineIndex] + IndentWidth;
        foreach (var token in line.Tokens)
        {
            if (token.Kind == SyntaxKind.EndOfLine)
                break;
            var at = tree.GetLineIndex(token.Span.Start);
            if (at != current)
            {
                for (var comment = current + 1; comment < at; comment++)
                    indents[comment] = margins[comment] = Inside();
                var closes = token.Kind is SyntaxKind.CloseParen or SyntaxKind.CloseBracket && opened.Count > 0;
                indents[at] = margins[at] = closes ? margins[opened.Peek()] : Inside();
                current = at;
            }
            if (token.Kind is SyntaxKind.OpenParen or SyntaxKind.OpenBracket)
                opened.Push(at);
            else if (token.Kind is SyntaxKind.CloseParen or SyntaxKind.CloseBracket && opened.Count > 0)
                opened.Pop();
        }
        for (var comment = current + 1; comment <= line.LastLineIndex; comment++)
            indents[comment] = Inside();
    }

    /// <summary>
    /// Records in <paramref name="depths"/> how many blocks hold each line, at the line of the file
    /// it starts on. Top-level lines have depth 0. A block's opening line, and its closing line if
    /// it has one, count as outside it.
    /// </summary>
    private static void Walk(SyntaxNode node, int depth, int[] depths)
    {
        // A region (a file-level `.segment NAME` with no brace) holds the rest of its segment.
        // It is the only block whose contents are not indented past its opening line.
        var block = node as BlockSyntax;
        var inner = block is null || block.BlockKind == BlockKind.Region ? depth : depth + 1;
        var members = node.ChildNodes;
        for (var i = 0; i < members.Length; i++)
        {
            var own = block is not null && (i == 0 || (block.HasCloser && i == members.Length - 1)) ? depth : inner;
            if (members[i] is BlockSyntax child)
                Walk(child, own, depths);
            else
                depths[members[i].LineIndex] = own;
        }
    }

    /// <summary>
    /// Returns the width of the text of the 0-based line <paramref name="line"/> of the file,
    /// without the line break that ends it.
    /// </summary>
    private static int Width(SyntaxTree tree, int line)
    {
        var start = tree.LineStarts[line];
        var end = tree.GetLineEnd(line);
        while (end > start && tree.Text[end - 1] is '\r' or '\n')
            end--;
        return end - start;
    }

    /// <summary>
    /// Returns where the comment at the end of the 0-based line <paramref name="index"/> of the file
    /// starts, or null when it has none.
    /// </summary>
    private static int? CommentStart(SyntaxTree tree, LineSyntax line, int index)
    {
        var start = tree.LineStarts[index];
        var end = start + Width(tree, index);
        foreach (var token in line.Tokens)
        {
            foreach (var trivia in token.LeadingTrivia.Concat(token.TrailingTrivia))
            {
                if (trivia.Kind == SyntaxKind.CommentTrivia && trivia.Position >= start && trivia.Position < end)
                    return trivia.Position;
            }
        }
        return null;
    }

    /// <summary>
    /// Returns the offset of token <paramref name="index"/>'s text from the start of its line.
    /// </summary>
    private static int TextOffset(LineSyntax line, int index) => line.Tokens[index].Span.Start - line.Position;

    /// <summary>
    /// Checks whether the line declares a cheap local, which goes at its routine's margin.
    /// </summary>
    private static bool IsCheapLocal(LineSyntax line) =>
        line.LineKind == LineKind.Label && line.Tokens[0].Kind == SyntaxKind.CheapLocal;

    /// <summary>
    /// Returns the token index of the <c>:</c> after the name a data line declares, or -1 if the
    /// line declares no data. A run aligns its directives on the column after this colon. Only
    /// directives, if anything, may precede the name, and a directive must follow its colon. That
    /// matches a member of a layout or a <c>.data</c> line, either one optionally preceded by
    /// <c>.export</c>. A <c>:</c> anywhere else is an address-size prefix or the start of a
    /// signature, and neither is followed by a directive.
    /// </summary>
    private static int NamedData(LineSyntax line)
    {
        if (line.LineKind is not (LineKind.Label or LineKind.Directive))
            return -1;

        var tokens = line.Tokens;
        var name = 0;
        while (tokens[name].Kind == SyntaxKind.Directive)
        {
            // An `.import` line also has an element type after its colon and can declare
            // several items, so it is not one declaration per line and gets no aligned column.
            if (tokens[name].DirectiveKind == DirectiveKind.Import)
                return -1;
            name++;
        }
        var colon = name + 1;

        // A member may be named after a register or a mnemonic, since it is only ever reached
        // through `::`. So any token that a line kind reads as a name counts as a name here.
        return tokens[name].Kind is SyntaxKind.Identifier or SyntaxKind.CheapLocal
                or SyntaxKind.Register or SyntaxKind.Mnemonic
            && colon + 1 < tokens.Count
            && tokens[colon].Kind == SyntaxKind.Colon
            && tokens[colon + 1].Kind == SyntaxKind.Directive
                ? colon
                : -1;
    }

    /// <summary>
    /// Represents one line split for layout. A line of a run is its <see cref="Head"/>, the gap
    /// it lines up on, its <see cref="Tail"/> and its <see cref="Comment"/>. Any other line is its
    /// <see cref="Head"/> alone, which is the whole of its text after its indent.
    /// </summary>
    /// <param name="Indent">The line's indent, in spaces.</param>
    /// <param name="Run">The kind of run the line belongs to.</param>
    /// <param name="Head">The text before the gap the run lines up on, such as a name.</param>
    /// <param name="Operator">The <c>=</c> or <c>?=</c> a <c>.const</c> or member line lines up on, or null.</param>
    /// <param name="Tail">The text after the gap, without the trailing comment.</param>
    /// <param name="Comment">The trailing comment of a line of a run, or null.</param>
    /// <param name="OnlyComment">Whether the line holds only a comment, which does not end a run.</param>
    /// <param name="Unaligned">
    /// Whether the line is in a run but has no gap to line up, and so keeps its own spacing and
    /// does not widen the run's column.
    /// </param>
    private readonly record struct Piece(
        int Indent, Run Run, string Head, string? Operator, string Tail, string? Comment, bool OnlyComment, bool Unaligned);
}
