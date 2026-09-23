using System.Text;

namespace Norristown.Syntax;

/// <summary>
/// Formats an nt65 file into its single canonical layout, which has no options. Whitespace
/// between tokens means nothing to the language, so re-indenting a line, trimming what follows
/// its last token, and changing the gap between a data line's name and its directive cannot
/// change what any line means.
/// <para>
/// Labels are indented like any other line, at the margin of whatever holds them; the lines
/// inside a block are indented one step further than the line that opens it; a routine's cheap
/// locals are pulled back to the routine's own margin, so they read as the routine's structure
/// rather than as another instruction; and a run of consecutive named data lines is aligned so
/// that their directives start in one column. This is the layout the generated ca65 already uses.
/// </para>
/// </summary>
public static class Formatter
{
    /// <summary>How far one block indents what it holds.</summary>
    public const int IndentWidth = 4;

    /// <summary>The whitespace characters trimmed from both ends of a line before it is laid out.</summary>
    private static readonly char[] Blank = [' ', '\t'];

    /// <summary>The text of <paramref name="tree"/>, formatted.</summary>
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
    /// What formatting lines <paramref name="first"/> to <paramref name="last"/> would change,
    /// 0-based and inclusive, one replacement per line that changes, in order. The whole file is
    /// laid out either way, because a line's indentation depends on the blocks around it and a
    /// data line's alignment column depends on the rest of its run; only changes to the requested
    /// lines are returned.
    /// </summary>
    public static IReadOnlyList<TextChange> Changes(SyntaxTree tree, int first, int last)
    {
        var formatted = Formatted(tree);
        var changes = new List<TextChange>();
        for (var i = Math.Max(first, 0); i <= Math.Min(last, tree.LineCount - 1); i++)
        {
            var start = tree.LineStarts[i];
            var length = Width(tree.GetLine(i));
            if (!tree.Text.AsSpan(start, length).SequenceEqual(formatted[i]))
                changes.Add(new TextChange(start, length, formatted[i]));
        }
        return changes;
    }

    /// <summary>Every line of <paramref name="tree"/> as the formatter writes it, without its break.</summary>
    private static string[] Formatted(SyntaxTree tree)
    {
        var depths = new int[tree.LineCount];
        var at = 0;
        Walk(tree.Root, 0, depths, ref at);

        // Each line is split into its indent, the name it declares, and the rest. Only a named
        // data line gets a separate name, so that it can be aligned; every other line is kept
        // as one piece after its indent.
        var indents = new int[tree.LineCount];
        var names = new string?[tree.LineCount];
        var rest = new string[tree.LineCount];
        var comments = new bool[tree.LineCount];
        for (var i = 0; i < tree.LineCount; i++)
        {
            var line = tree.GetLine(i);
            var content = tree.Text.AsSpan(tree.LineStarts[i], Width(line)).TrimEnd(Blank);
            if (content.TrimStart(Blank).IsEmpty)
            {
                rest[i] = "";
                continue;
            }

            // A cheap local belongs to the routine around it, not to the run of instructions it
            // points into the middle of, so it is written at the routine's own margin.
            indents[i] = Math.Max(depths[i] - (IsCheapLocal(line) ? 1 : 0), 0) * IndentWidth;
            var colon = NamedData(line);
            if (colon < 0)
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
    /// The lines put together, with each run of named data lines padded out to the one column
    /// its longest name needs, so that a run reads as a column the way hand-written ca65 does.
    /// A comment-only line inside a run does not end it; any other line, an empty one included,
    /// does.
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
    /// Records in <paramref name="depths"/> how many blocks hold each line; top-level lines have
    /// depth 0. A block's opener line, and its closer line when it has one, count as outside it.
    /// </summary>
    private static void Walk(SyntaxNode node, int depth, int[] depths, ref int line)
    {
        // A region (a file-level `.segment NAME` with no brace) holds the rest of its segment,
        // and it is the one block whose contents are not indented past its opening line.
        var block = node as BlockSyntax;
        var inner = block is null || block.BlockKind == BlockKind.Region ? depth : depth + 1;
        var members = node.ChildNodes;
        for (var i = 0; i < members.Length; i++)
        {
            var own = block is not null && (i == 0 || (block.HasCloser && i == members.Length - 1)) ? depth : inner;
            if (members[i] is BlockSyntax child)
                Walk(child, own, depths, ref line);
            else
                depths[line++] = own;
        }
    }

    /// <summary>The width of a line's text, without the break that ends it.</summary>
    private static int Width(LineSyntax line) => line.FullSpan.Length - line.EndOfLineToken.Text.Length;

    /// <summary>Offset of token <paramref name="index"/>'s text from the start of its line.</summary>
    private static int TextOffset(LineSyntax line, int index) => line.Tokens[index].Span.Start - line.Position;

    /// <summary>Whether the line declares a cheap local, which sits at its routine's margin.</summary>
    private static bool IsCheapLocal(LineSyntax line) =>
        line.LineKind == LineKind.Label && line.Tokens[0].Kind == SyntaxKind.CheapLocal;

    /// <summary>
    /// The token index of the <c>:</c> after the name a data line declares, or -1 if the line
    /// declares no data; a run aligns its directives on the column after this colon. The name
    /// must be preceded only by directives (if anything) and its colon followed by a directive:
    /// a member of a layout, a <c>.data</c> line, or either with <c>.export</c> in front. A
    /// <c>:</c> anywhere else is an address-size prefix or the start of a signature, and neither
    /// is followed by a directive.
    /// </summary>
    private static int NamedData(LineSyntax line)
    {
        if (line.LineKind is not (LineKind.Label or LineKind.Directive))
            return -1;

        var tokens = line.Tokens;
        var name = 0;
        while (tokens[name].Kind == SyntaxKind.Directive)
        {
            // An `.import` line also writes an element type after its colon and can declare
            // several items, so it is not one declaration per line and gets no aligned column.
            if (tokens[name].Text.Equals(".import", StringComparison.OrdinalIgnoreCase))
                return -1;
            name++;
        }
        var colon = name + 1;

        // A member may be named for a register or a mnemonic, since it is only ever reached
        // through `::`, so a name here is whatever a line kind reads as one.
        return tokens[name].Kind is SyntaxKind.Identifier or SyntaxKind.CheapLocal
                or SyntaxKind.Register or SyntaxKind.Mnemonic
            && colon + 1 < tokens.Count
            && tokens[colon].Kind == SyntaxKind.Colon
            && tokens[colon + 1].Kind == SyntaxKind.Directive
                ? colon
                : -1;
    }
}
