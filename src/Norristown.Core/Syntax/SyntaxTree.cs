using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>
/// One file's syntax: its lines, each lexed and parsed on its own, and the block structure
/// over them. A tree is immutable; <see cref="WithChange"/> gives the tree for an edited
/// text, reusing the green lines and statements the edit did not touch.
/// </summary>
public sealed class SyntaxTree
{
    private readonly Lazy<IReadOnlyList<Diagnostic>> diagnostics;
    private readonly ImmutableArray<Parser.Result> statements;
    private FileSyntax? root;

    private SyntaxTree(string path, string text, ImmutableArray<int> lineStarts, ImmutableArray<GreenLine> lines)
    {
        Path = path;
        Text = text;
        LineStarts = lineStarts;
        Lines = lines;
        var blockErrors = new List<Blocks.Error>();
        Green = Blocks.Build(lines, blockErrors);

        // Blocks come first because a line's syntax depends on the kind of block around it.
        // Nothing else about the line does, so a line that kept its tokens and its
        // surroundings across an edit keeps the statement it already has.
        var parsed = new Parser.Result[lines.Length];
        var line = 0;
        ParseLines(Green, BlockKind.None, parsed, ref line);
        statements = ImmutableCollectionsMarshal.AsImmutableArray(parsed);
        diagnostics = new(() => CollectDiagnostics(blockErrors));
    }

    /// <summary>The file's logical path, as it appears in diagnostics.</summary>
    public string Path { get; }

    /// <summary>The file's text.</summary>
    public string Text { get; }

    /// <summary>One green line per source line. A text with n line breaks has n + 1 lines.</summary>
    public ImmutableArray<GreenLine> Lines { get; }

    /// <summary>The offset in <see cref="Text"/> where each line starts.</summary>
    public ImmutableArray<int> LineStarts { get; }

    /// <summary>The file's lines and blocks.</summary>
    public GreenFile Green { get; }

    /// <summary>The root node, created on first use.</summary>
    public FileSyntax Root => root ??= new FileSyntax(this, null, Green, 0);

    /// <summary>Lexical, block-structure and parse errors, ordered by line and column.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics => diagnostics.Value;

    /// <summary>Parses a source file.</summary>
    public static SyntaxTree Parse(SourceFile file) => Parse(file.Path, file.Text);

    /// <summary>Parses <paramref name="text"/> as the file at <paramref name="path"/>.</summary>
    public static SyntaxTree Parse(string path, string text)
    {
        var starts = SplitLines(text);
        var lines = ImmutableArray.CreateBuilder<GreenLine>(starts.Length);
        for (var i = 0; i < starts.Length; i++)
            lines.Add(Lexer.LexLine(LineText(text, starts, i)));
        return new SyntaxTree(path, text, starts, lines.MoveToImmutable());
    }

    /// <summary>
    /// The tree for this text with <paramref name="change"/> applied. Only the lines the
    /// change touches are lexed again; every other line keeps its green node.
    /// </summary>
    public SyntaxTree WithChange(TextChange change)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(change.Start);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(change.Start + change.Length, Text.Length);

        var text = string.Concat(Text.AsSpan(0, change.Start), change.NewText, Text.AsSpan(change.Start + change.Length));
        var starts = SplitLines(text);
        var oldEnd = change.Start + change.Length;
        var delta = change.NewText.Length - change.Length;
        int oldCount = LineStarts.Length, newCount = starts.Length;

        // A line keeps its node when all of its text lies outside the change and the new text
        // splits it at the same place, which also covers a \r\n joined or split by the edit.
        var prefix = 0;
        while (prefix < oldCount && prefix < newCount
            && LineEnd(Text, LineStarts, prefix) <= change.Start
            && LineEnd(text, starts, prefix) == LineEnd(Text, LineStarts, prefix))
        {
            prefix++;
        }
        var suffix = 0;
        while (suffix < oldCount - prefix && suffix < newCount - prefix)
        {
            int oldLine = oldCount - 1 - suffix, newLine = newCount - 1 - suffix;
            if (LineStarts[oldLine] < oldEnd || starts[newLine] != LineStarts[oldLine] + delta
                || LineEnd(text, starts, newLine) != LineEnd(Text, LineStarts, oldLine) + delta)
            {
                break;
            }
            suffix++;
        }

        var lines = ImmutableArray.CreateBuilder<GreenLine>(newCount);
        lines.AddRange(Lines, prefix);
        for (var i = prefix; i < newCount - suffix; i++)
            lines.Add(Lexer.LexLine(LineText(text, starts, i)));
        for (var i = oldCount - suffix; i < oldCount; i++)
            lines.Add(Lines[i]);
        return new SyntaxTree(Path, text, starts, lines.MoveToImmutable());
    }

    /// <summary>The statement parsed from line <paramref name="line"/>, 0-based.</summary>
    public GreenNode Statement(int line) => statements[line].Node;

    /// <summary>
    /// Everything line <paramref name="line"/> parsed to, 0-based: its statement and the pieces
    /// the line holds rather than the statement.
    /// </summary>
    internal Parser.Result Parsed(int line) => statements[line];

    /// <summary>The 0-based line holding <paramref name="position"/>.</summary>
    public int GetLineIndex(int position)
    {
        var index = LineStarts.BinarySearch(position);
        return index >= 0 ? index : ~index - 1;
    }

    /// <summary>
    /// The offset of a 0-based line and character, clamped to the text. An editor may name a
    /// position past the end of a line or of the file, and that is not an error here.
    /// </summary>
    public int GetPosition(int line, int character)
    {
        if (line < 0)
            return 0;
        if (line >= LineStarts.Length)
            return Text.Length;
        var start = LineStarts[line];
        var end = line + 1 < LineStarts.Length ? LineStarts[line + 1] : Text.Length;
        return character <= 0 ? start : Math.Min(start + character, end);
    }

    /// <summary>A diagnostic span for a range on one line.</summary>
    public Span GetSpan(TextSpan span)
    {
        var line = GetLineIndex(span.Start);
        var column = span.Start - LineStarts[line] + 1;
        return new Span(Path, line + 1, column, column + span.Length);
    }

    /// <summary>Line starts. <c>\r\n</c>, <c>\n</c> and a lone <c>\r</c> each end a line, as in LSP.</summary>
    private static ImmutableArray<int> SplitLines(string text)
    {
        var starts = ImmutableArray.CreateBuilder<int>();
        starts.Add(0);
        var span = text.AsSpan();
        var offset = 0;
        while (true)
        {
            var i = span[offset..].IndexOfAny('\r', '\n');
            if (i < 0)
                break;
            offset += i;
            offset += span[offset] == '\r' && offset + 1 < span.Length && span[offset + 1] == '\n' ? 2 : 1;
            starts.Add(offset);
        }
        return starts.ToImmutable();
    }

    /// <summary>Parses every line under <paramref name="node"/>, each in the block kind around it.</summary>
    private static void ParseLines(GreenNode node, BlockKind context, Parser.Result[] parsed, ref int line)
    {
        for (var i = 0; i < node.SlotCount; i++)
        {
            // A block's opener and closer lines sit inside it, so they are parsed in its own
            // kind: `}` is the one line a block with a grammar of its own still reads the
            // ordinary way.
            // A conditional or a repetition inside a data body holds values too, and a
            // conditional inside an enum holds members, so their lines read the way the body's
            // own do.
            if (node.GetSlot(i) is GreenBlock block)
                ParseLines(block, Within(context, block.BlockKind), parsed, ref line);
            else
                parsed[line++] = ((GreenLine)node.GetSlot(i)!).Parse(context);
        }
    }

    /// <summary>
    /// The kind a block's lines read in: its own, unless it is a conditional or a repetition
    /// inside a body with a line grammar of its own, whose lines are that body's.
    /// </summary>
    private static BlockKind Within(BlockKind context, BlockKind block) => (context, block) switch
    {
        (BlockKind.DataBody, BlockKind.If or BlockKind.Repeat or BlockKind.Each) => BlockKind.DataBody,
        (BlockKind.Enum, BlockKind.If) => BlockKind.Enum,
        _ => block,
    };

    private static int LineEnd(string text, ImmutableArray<int> starts, int line) =>
        line + 1 < starts.Length ? starts[line + 1] : text.Length;

    private static ReadOnlySpan<char> LineText(string text, ImmutableArray<int> starts, int line) =>
        text.AsSpan(starts[line], LineEnd(text, starts, line) - starts[line]);

    private List<Diagnostic> CollectDiagnostics(List<Blocks.Error> blockErrors)
    {
        var result = new List<Diagnostic>();
        Diagnostic At(int line, int token, string message, DiagnosticFix? fix = null)
        {
            var green = Lines[line];
            var tokens = green.Tokens;

            // A diagnostic on the end-of-line token is about something the line does not
            // have, so it belongs where that something would have been written: just past
            // the last real token, ahead of the whitespace and comment that follow it. The
            // line break itself is the wrong place — the caret would drift to the right as
            // trailing spaces were typed, and sit past the end of a trailing comment.
            if (tokens[token].Kind == SyntaxKind.EndOfLine && token > 0)
            {
                var caret = green.TextOffset(token - 1) + tokens[token - 1].Text.Length + 1;
                return new Diagnostic(new Span(Path, line + 1, caret, caret), Severity.Error, message) { Fix = fix };
            }

            // Otherwise the token is the problem, and the diagnostic covers it. A line with
            // no tokens at all leaves a caret where its text would start.
            var column = green.TextOffset(token) + 1;
            var width = tokens[token].Kind == SyntaxKind.EndOfLine ? 0 : tokens[token].Text.Length;
            return new Diagnostic(new Span(Path, line + 1, column, column + width), Severity.Error, message) { Fix = fix };
        }

        for (var i = 0; i < Lines.Length; i++)
        {
            var tokens = Lines[i].Tokens;
            for (var t = 0; t < tokens.Length; t++)
            {
                if (tokens[t].Error is { } error)
                    result.Add(At(i, t, error));
            }
            foreach (var error in statements[i].Errors)
                result.Add(At(i, error.Token, error.Message, error.Fix));
        }
        result.AddRange(blockErrors.Select(e => At(e.Line, e.Token, e.Message)));
        return [.. result
            .OrderBy(d => d.Span.Line)
            .ThenBy(d => d.Span.StartColumn)
            .ThenBy(d => d.Message, StringComparer.Ordinal)];
    }
}
