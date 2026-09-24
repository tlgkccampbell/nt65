using System.Text;

namespace Norristown.Syntax;

/// <summary>
/// Formats an nt65 file into its single canonical layout, which has no options. Whitespace
/// between tokens has no meaning in the language. So re-indenting a line, trimming the text after
/// its last token, and changing the gap between a data line's name and its directive cannot
/// change what any line means.
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
/// A run of consecutive named data lines is aligned so that their directives start in one column.
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

    /// <summary>The whitespace characters trimmed from both ends of a line before it is laid out.</summary>
    private static readonly char[] Blank = [' ', '\t'];

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
    /// line's indentation depends on the blocks around it, and a data line's alignment column
    /// depends on the rest of its run. Only changes to the requested lines are returned.
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

        // Each line is split into its indent, the name it declares, and the rest. Only a named
        // data line gets a separate name, so that it can be aligned; every other line is kept
        // as one piece after its indent.
        var indents = new int[tree.LineCount];
        var continued = new Dictionary<int, int>();
        for (var i = 0; i < tree.LineCount; i++)
        {
            var line = tree.GetLine(i);
            if (line.LineIndex == i && line.LastLineIndex > i)
                Continued(tree, line, Math.Max(depths[i] - (IsCheapLocal(line) ? 1 : 0), 0) * IndentWidth, continued);
        }
        var names = new string?[tree.LineCount];
        var rest = new string[tree.LineCount];
        var comments = new bool[tree.LineCount];
        for (var i = 0; i < tree.LineCount; i++)
        {
            var line = tree.GetLine(i);
            var content = tree.Text.AsSpan(tree.LineStarts[i], Width(tree, i)).TrimEnd(Blank);
            if (content.TrimStart(Blank).IsEmpty)
            {
                rest[i] = "";
                continue;
            }

            // A line that continues an expression is indented by the brackets still open at its
            // start, which were worked out with the line the expression starts on.
            if (line.LineIndex != i)
            {
                indents[i] = continued.GetValueOrDefault(i, (depths[line.LineIndex] + 1) * IndentWidth);
                rest[i] = content.TrimStart(Blank).ToString();
                continue;
            }

            // A cheap local belongs to the routine around it, not to the run of instructions it
            // points into the middle of, so it goes at the routine's own margin.
            indents[i] = Math.Max(depths[i] - (IsCheapLocal(line) ? 1 : 0), 0) * IndentWidth;
            var colon = NamedData(line);
            if (colon < 0 || TextOffset(line, colon + 1) > content.Length)
            {
                rest[i] = content.TrimStart(Blank).ToString();
                comments[i] = line.LineKind == LineKind.Blank;
                continue;
            }
            names[i] = content[TextOffset(line, 0)..(TextOffset(line, colon) + 1)].ToString();
            rest[i] = content[TextOffset(line, colon + 1)..].ToString();
        }
        return Assembled(indents, names, rest, comments);
    }

    /// <summary>
    /// Assembles the formatted lines. Each run of named data lines is padded to the column that
    /// its longest name needs, so that a run reads as a column, as in hand-written ca65. A
    /// comment-only line inside a run does not end it, but any other line does, including an
    /// empty one.
    /// </summary>
    private static string[] Assembled(int[] indents, string?[] names, string[] rest, bool[] comments)
    {
        var formatted = new string[rest.Length];
        for (var i = 0; i < rest.Length; i++)
        {
            formatted[i] = rest[i].Length == 0 ? "" : new string(' ', indents[i]) + rest[i];
            if (names[i] is null)
                continue;

            var end = i;
            for (var next = i + 1; next < rest.Length; next++)
            {
                if (names[next] is not null)
                    end = next;
                else if (!comments[next])
                    break;
            }
            var column = Enumerable.Range(i, end - i + 1)
                .Where(line => names[line] is not null)
                .Max(line => indents[line] + names[line]!.Length) + 1;
            for (; i <= end; i++)
            {
                var name = new string(' ', indents[i]) + names[i];
                formatted[i] = names[i] is null ? name + rest[i] : name + new string(' ', column - name.Length) + rest[i];
            }
            i--;
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
}
