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

    // The file's lines and blocks, which the root is the red node over. A tree of one built node
    // has neither, and answers over that node instead.
    private readonly GreenFile? green;
    private readonly ImmutableArray<Parser.Result> statements;
    private readonly ImmutableArray<Blocks.Error> blockErrors;
    private readonly bool[] reported;
    private FileSyntax? root;

    // The parse a line is to keep instead of the one its tokens give, for the lines an annotated
    // rewrite reattached its annotations to; null everywhere else, and empty where there are
    // none at all. It rides along an edit for every line that keeps its green node, which is what
    // makes an annotation survive an edit elsewhere in the file and go with a line parsed again.
    private readonly ImmutableArray<Parser.Result?> carried;

    // Which lines hold an annotation, allocated only once something does. A green line holds the
    // tokens the lexer read and not the statement they parse to, so, as with the diagnostics, no
    // flag on the line itself could answer for the line.
    private readonly bool[]? annotated;

    /// <summary>
    /// The tree a node built by <see cref="SyntaxFactory"/> belongs to: the node's own text and
    /// nothing else, so that its spans, its trivia and what it says are read the same way a
    /// node of a file's are. It has no lines and no <see cref="Root"/>; a rewrite that puts the
    /// node into a file gives a tree of that file.
    /// </summary>
    /// <param name="built">The node the tree is of.</param>
    private SyntaxTree(GreenNode built)
    {
        Path = "";
        Text = built.ToFullString();
        LineStarts = SplitLines(Text);
        Lines = [];
        statements = [];
        blockErrors = [];
        reported = [];
        diagnostics = new(() =>
        {
            var result = new List<Diagnostic>();
            Collect(built, 0, result);
            return result;
        });
    }

    private SyntaxTree(
        string path, string text, ImmutableArray<int> lineStarts, ImmutableArray<GreenLine> lines,
        ImmutableArray<Parser.Result?> carried)
    {
        Path = path;
        Text = text;
        LineStarts = lineStarts;
        Lines = lines;
        this.carried = carried;
        var errors = new List<Blocks.Error>();
        green = Blocks.Build(lines, errors);
        blockErrors = [.. errors];

        // Blocks come first because a line's syntax depends on the kind of block around it.
        // Nothing else about the line does, so a line that kept its tokens and its
        // surroundings across an edit keeps the statement it already has.
        var parsed = new Parser.Result[lines.Length];
        var line = 0;
        ParseLines(green, BlockKind.None, parsed, carried, ref line);
        statements = ImmutableCollectionsMarshal.AsImmutableArray(parsed);

        // Which lines have something to say is worked out here, once, so that a node asked
        // whether it holds a diagnostic answers by reading flags rather than by walking. A green
        // line does not hold what it parses to, so no flag on it could answer for the line.
        reported = new bool[lines.Length];
        for (var i = 0; i < parsed.Length; i++)
        {
            reported[i] = Said(parsed[i], lines[i]);

            // Annotations are rare — a file has none until a rewrite tags something — so the
            // flags for them are not there at all until one does.
            if (Marked(parsed[i], lines[i]))
                (annotated ??= new bool[lines.Length])[i] = true;
        }
        foreach (var error in blockErrors)
            reported[error.Line] = true;
        diagnostics = new(() => CollectRange(0, lines.Length - 1));
    }

    /// <summary>The file's logical path, as it appears in diagnostics.</summary>
    public string Path { get; }

    /// <summary>The file's text.</summary>
    public string Text { get; }

    /// <summary>The offset in <see cref="Text"/> where each line starts.</summary>
    public ImmutableArray<int> LineStarts { get; }

    /// <summary>One green line per source line. A text with n line breaks has n + 1 lines.</summary>
    internal ImmutableArray<GreenLine> Lines { get; }

    /// <summary>
    /// The root node, created on first use, and the same one whoever asks first. A tree of one
    /// node built by <see cref="SyntaxFactory"/> is no file and has none.
    /// </summary>
    public FileSyntax Root =>
        root ?? Interlocked.CompareExchange(
            ref root,
            new FileSyntax(this, null, green ?? throw new InvalidOperationException("a built node is no file"), 0),
            null) ?? root;

    /// <summary>Lexical, block-structure and parse errors, ordered by line and column.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics => diagnostics.Value;

    /// <summary>How many lines the file has. A text with n line breaks has n + 1 lines.</summary>
    public int LineCount => LineStarts.Length;

    /// <summary>Parses a source file.</summary>
    public static SyntaxTree Parse(SourceFile file) => Parse(file.Path, file.Text);

    /// <summary>
    /// Where each line of <paramref name="text"/> starts, without parsing it. It is what
    /// <see cref="LineStarts"/> holds, for a caller placing a line and a column in a text it
    /// has not made a tree of — an editor's edits, which name places in the text each of the
    /// ones before it left.
    /// </summary>
    public static ImmutableArray<int> LineOffsets(string text) => SplitLines(text);

    /// <summary>Parses <paramref name="text"/> as the file at <paramref name="path"/>.</summary>
    public static SyntaxTree Parse(string path, string text)
    {
        var starts = SplitLines(text);
        var lines = ImmutableArray.CreateBuilder<GreenLine>(starts.Length);
        for (var i = 0; i < starts.Length; i++)
            lines.Add(Lexer.LexLine(LineText(text, starts, i)));
        return new SyntaxTree(path, text, starts, lines.MoveToImmutable(), default);
    }

    /// <summary>
    /// The red node for <paramref name="built"/>, in a tree of that node alone. It is what
    /// <see cref="SyntaxFactory"/> hands back and what an <c>Update</c> makes: a node with no
    /// file around it, whose text is its own.
    /// </summary>
    /// <param name="built">The green node just built.</param>
    internal static SyntaxNode Detached(GreenNode built) => built.CreateRed(new SyntaxTree(built), null, 0);

    /// <summary>
    /// The tree for this text with <paramref name="change"/> applied. Only the lines the
    /// change touches are lexed again; every other line keeps its green node.
    /// </summary>
    public SyntaxTree WithChange(TextChange change) => WithChanges([change]);

    /// <summary>
    /// The tree for this text with <paramref name="changes"/> applied in order, each to what
    /// the one before it left, which is how an editor sends the edits of one keystroke. The
    /// text is split into lines and the tree rebuilt once for the lot, rather than once per
    /// change: only the lines between the first and the last change are lexed again.
    /// </summary>
    public SyntaxTree WithChanges(IReadOnlyList<TextChange> changes)
    {
        if (changes.Count == 0)
            return this;

        // The text before the first change and the text after the last are the same in the
        // tree this gives as in this one, whatever the changes in between did, so they are
        // what says which lines keep their nodes: `head` characters at the start and `tail`
        // at the end. A change reaching further out than the ones before it widens the gap.
        var text = Text;
        int head = text.Length, tail = text.Length;
        foreach (var change in changes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(change.Start);
            ArgumentOutOfRangeException.ThrowIfNegative(change.Length);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(change.Start + change.Length, text.Length);
            head = Math.Min(head, change.Start);
            tail = Math.Min(tail, text.Length - change.Start - change.Length);
            text = string.Concat(
                text.AsSpan(0, change.Start), change.NewText, text.AsSpan(change.Start + change.Length));
        }

        var starts = SplitLines(text);
        var oldEnd = Text.Length - tail;
        var delta = text.Length - Text.Length;
        int oldCount = LineStarts.Length, newCount = starts.Length;

        // A line keeps its node when all of its text lies outside the change and the new text
        // splits it at the same place, which also covers a \r\n joined or split by the edit.
        var prefix = 0;
        while (prefix < oldCount && prefix < newCount
            && LineEnd(Text, LineStarts, prefix) <= head
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
        return new SyntaxTree(Path, text, starts, lines.MoveToImmutable(), Carried(prefix, suffix, newCount));
    }

    /// <summary>
    /// The annotated parses this tree keeps, for the lines of the tree an edit gives: a line that
    /// keeps its green node keeps them, wherever the edit moved it, and a line lexed again does
    /// not. It is the same rule the statements themselves follow, and it is why an annotation
    /// survives an edit somewhere else in the file and is gone from a line typed over.
    /// </summary>
    /// <param name="prefix">How many lines at the start of the file the edit left alone.</param>
    /// <param name="suffix">How many lines at the end of it the edit left alone.</param>
    /// <param name="newCount">How many lines the edited file has.</param>
    private ImmutableArray<Parser.Result?> Carried(int prefix, int suffix, int newCount)
    {
        if (carried.IsDefaultOrEmpty)
            return default;
        var kept = new Parser.Result?[newCount];
        var any = false;
        for (var i = 0; i < prefix; i++)
            any |= (kept[i] = carried[i]) is not null;
        for (var i = 0; i < suffix; i++)
            any |= (kept[newCount - 1 - i] = carried[carried.Length - 1 - i]) is not null;
        return any ? ImmutableCollectionsMarshal.AsImmutableArray(kept) : default;
    }

    /// <summary>
    /// This tree with <paramref name="kept"/> as the parse of the lines that have one, which is
    /// how a rewrite puts the annotations it carried across a reparse back on the tree. Nothing
    /// else moves: the text and the lines are this tree's, and a kept parse is the line's own
    /// with its annotations on.
    /// </summary>
    /// <param name="kept">One entry per line: a parse to keep, or null to read the line's own.</param>
    internal SyntaxTree WithCarried(ImmutableArray<Parser.Result?> kept) =>
        new(Path, Text, LineStarts, Lines, kept);

    /// <summary>
    /// Everything line <paramref name="line"/> parsed to, 0-based: its statement and the pieces
    /// the line holds rather than the statement.
    /// </summary>
    internal Parser.Result Parsed(int line) => statements[line];

    /// <summary>
    /// Line <paramref name="line"/> of the file, 0-based, as a node of the tree: what it parsed
    /// to, and the tokens it is written with. It is the same node the walk down from
    /// <see cref="Root"/> reaches, so it knows the blocks it is written in.
    /// </summary>
    /// <param name="line">The 0-based line.</param>
    public LineSyntax GetLine(int line) => Root.Lines[line];

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
    private static void ParseLines(
        GreenNode node, BlockKind context, Parser.Result[] parsed, ImmutableArray<Parser.Result?> carried, ref int line)
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
            {
                ParseLines(block, Within(context, block.BlockKind), parsed, carried, ref line);
                continue;
            }

            // A line an annotated rewrite reattached its annotations to keeps that parse, which
            // is the line's own with the annotations on it. A line whose surroundings have since
            // changed is read again in the kind of block it is in now, and the annotations go
            // with the parse they were on.
            var at = line++;
            parsed[at] = !carried.IsDefaultOrEmpty && carried[at] is { } kept && kept.Context == context
                ? kept
                : ((GreenLine)node.GetSlot(i)!).Parse(context);
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

    /// <summary>
    /// Whether a parsed line has anything to say: a lexical error on one of its tokens, or
    /// something the parser said about a piece of what they parse to. A green line holds the
    /// tokens the lexer read and not those pieces, so the line's answer is both of theirs.
    /// </summary>
    private static bool Said(Parser.Result parsed, GreenLine line) =>
        line.ContainsDiagnostics
        || parsed.Node.ContainsDiagnostics
        || parsed.ExportKeyword is { ContainsDiagnostics: true }
        || parsed.SkippedTokens is { ContainsDiagnostics: true };

    /// <summary>
    /// Whether anything on a parsed line carries an annotation, which, as with the diagnostics,
    /// is both the line's own tokens and what they parse to.
    /// </summary>
    private static bool Marked(Parser.Result parsed, GreenLine line) =>
        line.ContainsAnnotations
        || parsed.Node.ContainsAnnotations
        || parsed.ExportKeyword is { ContainsAnnotations: true }
        || parsed.SkippedTokens is { ContainsAnnotations: true };

    /// <summary>
    /// The diagnostics <paramref name="green"/> and everything under it carry, as spans in the
    /// file, where <paramref name="position"/> is where the node starts. Only a subtree that says
    /// it holds one is walked at all.
    /// </summary>
    internal void Collect(GreenNode green, int position, List<Diagnostic> result)
    {
        if (!green.ContainsDiagnostics)
            return;
        foreach (var diagnostic in green.Diagnostics)
        {
            var span = GetSpan(new TextSpan(position + diagnostic.Offset, diagnostic.Width));
            result.Add(new Diagnostic(span, diagnostic.Message) { Fix = diagnostic.Fix });
        }
        for (var i = 0; i < green.SlotCount; i++)
        {
            if (green.GetSlot(i) is not { } slot)
                continue;
            Collect(slot, position, result);
            position += slot.FullWidth;
        }
    }

    /// <summary>
    /// Whether any line from <paramref name="first"/> to <paramref name="last"/>, both 0-based and
    /// inclusive, has a diagnostic on it, which is what a block and a file answer for.
    /// </summary>
    internal bool LinesContainDiagnostics(int first, int last)
    {
        for (var i = first; i <= last; i++)
        {
            if (reported[i])
                return true;
        }
        return false;
    }

    /// <summary>
    /// Whether any line from <paramref name="first"/> to <paramref name="last"/>, both 0-based and
    /// inclusive, carries an annotation, which is what a line, a block and the file answer for.
    /// </summary>
    internal bool LinesContainAnnotations(int first, int last)
    {
        if (annotated is null)
            return false;
        for (var i = first; i <= last; i++)
        {
            if (annotated[i])
                return true;
        }
        return false;
    }

    /// <summary>
    /// The diagnostics of the lines from <paramref name="first"/> to <paramref name="last"/>, both
    /// 0-based and inclusive, added to <paramref name="result"/> in source order.
    /// </summary>
    internal void CollectLines(int first, int last, List<Diagnostic> result) =>
        result.AddRange(CollectRange(first, last));

    /// <summary>
    /// Every diagnostic on the lines from <paramref name="first"/> to <paramref name="last"/>,
    /// ordered by line and column. The lines that say nothing are skipped, so the whole file's
    /// answer costs a walk of the subtrees that hold one.
    /// </summary>
    private List<Diagnostic> CollectRange(int first, int last)
    {
        var result = new List<Diagnostic>();
        for (var i = first; i <= last; i++)
        {
            if (!reported[i])
                continue;

            // A line's tokens are its statement's as well, so the line itself is not walked:
            // the pieces it is written in hold every token of it exactly once between them, and
            // the statement holds besides them the missing tokens and the nodes that carry what
            // the parser said.
            var line = Lines[i];
            var parsed = statements[i];
            var at = LineStarts[i];
            if (parsed.ExportKeyword is { } export)
            {
                Collect(export, at, result);
                at += export.FullWidth;
            }
            Collect(parsed.Node, at, result);
            if (parsed.SkippedTokens is { } skipped)
                Collect(skipped, at + parsed.Node.FullWidth, result);

            // The line break is the line's own, and the last of its tokens.
            var end = line.Tokens[^1];
            Collect(end, LineStarts[i] + line.FullWidth - end.FullWidth, result);
        }

        // A block error is about the braces over the lines rather than about anything in one, so
        // it names its line and the token on it that the diagnostic covers.
        result.AddRange(blockErrors
            .Where(error => error.Line >= first && error.Line <= last)
            .Select(error =>
            {
                var token = Lines[error.Line].Tokens[error.Token];
                var column = Lines[error.Line].TextOffset(error.Token) + 1;
                var width = token.Kind == SyntaxKind.EndOfLine ? 0 : token.Text.Length;
                return new Diagnostic(new Span(Path, error.Line + 1, column, column + width), error.Message);
            }));
        return [.. result
            .OrderBy(d => d.Span.Line)
            .ThenBy(d => d.Span.StartColumn)
            .ThenBy(d => d.Message, StringComparer.Ordinal)
            .ThenBy(d => d.Id, StringComparer.Ordinal)];
    }
}
