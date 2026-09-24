using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>
/// Represents the syntax of one file. It holds the file's lines, each lexed and parsed on its
/// own, and the block structure over them. A tree is immutable. <see cref="WithChange"/> returns
/// the tree for an edited text, reusing the green lines and statements the edit did not touch.
/// </summary>
public sealed class SyntaxTree
{
    private readonly Lazy<IReadOnlyList<Diagnostic>> diagnostics;

    // The file's lines and blocks, over which the root is the red node. A tree holding a single
    // built node has neither, and answers questions from that node instead.
    private readonly GreenFile? green;
    private readonly ImmutableArray<Parser.Result> statements;
    private readonly ImmutableArray<Blocks.Error> blockErrors;
    private readonly bool[] reported;
    private FileSyntax? root;

    // For each line that an annotating rewrite reattached annotations to, this holds the parse to
    // use instead of parsing the line's tokens afresh. It is null for every other line, and the
    // array is default or empty when no line has such a parse. An edit keeps these entries for
    // every line that keeps its green node. That lets an annotation survive an edit elsewhere in
    // the file, and drops it when its own line is parsed again.
    private readonly ImmutableArray<Parser.Result?> keptParses;

    // Records which lines hold an annotation, and is allocated only once some line does. A green
    // line holds the tokens the lexer read and not the statement they parse to. So, as with the
    // diagnostics, a flag on the green line alone could not cover everything on the line.
    private readonly bool[]? annotated;

    /// <summary>
    /// Initializes the tree that a node built by <see cref="SyntaxFactory"/> belongs to. The tree's
    /// text is the node's own text and nothing else, so the node's spans, trivia and diagnostics
    /// are read the same way as a file node's. The tree has no lines and no <see cref="Root"/>. A
    /// rewrite that puts the node into a file produces a tree of that file.
    /// </summary>
    /// <param name="built">The node the tree contains.</param>
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
        ImmutableArray<Parser.Result?> keptParses)
    {
        Path = path;
        Text = text;
        LineStarts = lineStarts;
        Lines = lines;
        this.keptParses = keptParses;
        var errors = new List<Blocks.Error>();
        green = Blocks.Build(lines, errors);
        blockErrors = [.. errors];

        // Blocks come first because a line's syntax depends on the kind of block around it.
        // Nothing else about the line does, so a line that kept its tokens and its
        // surroundings across an edit keeps the statement it already has.
        var parsed = new Parser.Result[lines.Length];
        var line = 0;
        ParseLines(green, BlockKind.None, parsed, keptParses, ref line);
        statements = ImmutableCollectionsMarshal.AsImmutableArray(parsed);

        // Which lines have diagnostics is computed once, here, so that a node asked whether it
        // holds a diagnostic answers by reading flags rather than by walking. A green line does
        // not hold what it parses to, so a flag on the green line alone could not cover the line.
        reported = new bool[lines.Length];
        for (var i = 0; i < parsed.Length; i++)
        {
            reported[i] = HasDiagnostics(parsed[i], lines[i]);

            // Annotations are rare — a file has none until a rewrite tags something — so the
            // flags for them are not allocated until some line has an annotation.
            if (Marked(parsed[i], lines[i]))
                (annotated ??= new bool[lines.Length])[i] = true;
        }
        foreach (var error in blockErrors)
            reported[error.Line] = true;
        diagnostics = new(() => CollectRange(0, lines.Length - 1));
    }

    /// <summary>Gets the file's logical path, as it appears in diagnostics.</summary>
    public string Path { get; }

    /// <summary>Gets the file's text.</summary>
    public string Text { get; }

    /// <summary>Gets the offset in <see cref="Text"/> where each line starts.</summary>
    public ImmutableArray<int> LineStarts { get; }

    /// <summary>
    /// Gets the root node, which is created on first use; every caller gets the same instance. A
    /// tree holding a single node built by <see cref="SyntaxFactory"/> is not a file and has no
    /// root.
    /// </summary>
    public FileSyntax Root =>
        root ?? Interlocked.CompareExchange(
            ref root,
            new FileSyntax(this, null, green ?? throw new InvalidOperationException("a built node is no file"), 0),
            null) ?? root;

    /// <summary>
    /// Gets the lexical, block-structure and parse errors, ordered by line and column.
    /// </summary>
    public IReadOnlyList<Diagnostic> Diagnostics => diagnostics.Value;

    /// <summary>Gets the number of lines in the file. A text with n line breaks has n + 1 lines.</summary>
    public int LineCount => LineStarts.Length;

    /// <summary>
    /// Gets the green lines, one per source line. A text with n line breaks has n + 1 lines.
    /// </summary>
    internal ImmutableArray<GreenLine> Lines { get; }

    /// <summary>Parses a source file.</summary>
    public static SyntaxTree Parse(SourceFile file) => Parse(file.Path, file.Text);

    /// <summary>
    /// Returns the offset where each line of <paramref name="text"/> starts, without parsing it.
    /// The result is what <see cref="LineStarts"/> would hold. It serves a caller converting a
    /// line and column to an offset in a text it has not parsed, such as an editor applying a
    /// batch of edits, each of which names positions in the text the previous edit produced.
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
    /// Returns the 0-based line holding <paramref name="position"/>, given where each line starts
    /// as <see cref="LineOffsets"/> returns it.
    /// </summary>
    public static int GetLineIndex(ImmutableArray<int> lineStarts, int position)
    {
        var index = lineStarts.BinarySearch(position);
        return index >= 0 ? index : ~index - 1;
    }

    /// <summary>Returns the 0-based line holding <paramref name="position"/>.</summary>
    public int GetLineIndex(int position) => GetLineIndex(LineStarts, position);

    /// <summary>
    /// Returns the offset in <paramref name="text"/> of a 0-based line and character, given where
    /// each line starts as <see cref="LineOffsets"/> returns it. The offset is clamped to the
    /// text, since an editor may name a position past the end of a line or of the file, and that
    /// is not an error here.
    /// </summary>
    public static int GetPosition(string text, ImmutableArray<int> lineStarts, int line, int character)
    {
        if (line < 0)
            return 0;
        if (line >= lineStarts.Length)
            return text.Length;
        var start = lineStarts[line];
        return character <= 0 ? start : Math.Min(start + character, LineEnd(text, lineStarts, line));
    }

    /// <summary>
    /// Returns the offset of a 0-based line and character, clamped to the text. An editor may name
    /// a position past the end of a line or of the file, and that is not an error here.
    /// </summary>
    public int GetPosition(int line, int character) => GetPosition(Text, LineStarts, line, character);

    /// <summary>
    /// Returns the tree for this text with <paramref name="change"/> applied. Only the lines the
    /// change touches are lexed again; every other line keeps its green node.
    /// </summary>
    public SyntaxTree WithChange(TextChange change) => WithChanges([change]);

    /// <summary>
    /// Returns the tree for this text with <paramref name="changes"/> applied in order, each to
    /// the text the previous change produced. This is how an editor sends the edits of one
    /// keystroke. The text is split into lines and the tree rebuilt once for all the changes,
    /// rather than once per change, and only the lines between the first and the last change are
    /// lexed again.
    /// </summary>
    public SyntaxTree WithChanges(IReadOnlyList<TextChange> changes)
    {
        if (changes.Count == 0)
            return this;

        // The first `head` characters and the last `tail` characters are the same in the new
        // text as in this one, regardless of the changes in between, so they decide which lines
        // keep their green nodes. A change reaching further out than the ones before it widens
        // the changed region between them.
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
        return new SyntaxTree(Path, text, starts, lines.MoveToImmutable(), KeptParsesAfter(prefix, suffix, newCount));
    }

    /// <summary>
    /// Returns the annotated parses to keep in the tree an edit produces. A line that keeps its
    /// green node keeps its annotated parse, even if the edit moved it, and a line lexed again does
    /// not. The statements follow the same rule. As a result, an annotation survives an edit
    /// elsewhere in the file but is gone from a line that was typed over.
    /// </summary>
    /// <param name="prefix">The number of lines at the start of the file the edit left alone.</param>
    /// <param name="suffix">The number of lines at the end of the file the edit left alone.</param>
    /// <param name="newCount">The number of lines in the edited file.</param>
    private ImmutableArray<Parser.Result?> KeptParsesAfter(int prefix, int suffix, int newCount)
    {
        if (keptParses.IsDefaultOrEmpty)
            return default;
        var kept = new Parser.Result?[newCount];
        var any = false;
        for (var i = 0; i < prefix; i++)
            any |= (kept[i] = keptParses[i]) is not null;
        for (var i = 0; i < suffix; i++)
            any |= (kept[newCount - 1 - i] = keptParses[keptParses.Length - 1 - i]) is not null;
        return any ? ImmutableCollectionsMarshal.AsImmutableArray(kept) : default;
    }

    /// <summary>
    /// Returns the 0-based line <paramref name="line"/> of the file as a node of the tree, with
    /// what it parsed to and the tokens it contains. It is the same node the walk down from
    /// <see cref="Root"/> reaches, so it knows the blocks that contain it.
    /// </summary>
    /// <param name="line">The 0-based line.</param>
    public LineSyntax GetLine(int line) => Root.Lines[line];

    /// <summary>
    /// Returns the offset where the 0-based line <paramref name="line"/> ends, after its line
    /// break. That is where the next line starts, or the end of the text for the last line.
    /// </summary>
    public int GetLineEnd(int line) => LineEnd(Text, LineStarts, line);

    /// <summary>Returns the diagnostic span for a range on one line.</summary>
    public Span GetSpan(TextSpan span)
    {
        var line = GetLineIndex(span.Start);
        var column = span.Start - LineStarts[line] + 1;
        return new Span(Path, line + 1, column, column + span.Length);
    }

    /// <summary>
    /// Returns the red node for <paramref name="built"/>, in a tree that contains only that node.
    /// <see cref="SyntaxFactory"/> returns such nodes, and an <c>Update</c> creates them. The node
    /// has no file around it, and its text is its own.
    /// </summary>
    /// <param name="built">The green node just built.</param>
    internal static SyntaxNode Detached(GreenNode built) => built.CreateRed(new SyntaxTree(built), null, 0);

    /// <summary>
    /// Returns a copy of this tree that uses <paramref name="kept"/> as the parse of each line that
    /// has an entry. <see cref="AnnotationCarrier"/> uses this method to restore the annotations a
    /// rewrite carried across a reparse. Nothing else changes. The text and the lines are this tree's, and a kept parse is
    /// the line's own parse with the annotations added.
    /// </summary>
    /// <param name="kept">
    /// One entry per line, holding either a parse to keep or null to use the line's own parse.
    /// </param>
    internal SyntaxTree WithKeptParses(ImmutableArray<Parser.Result?> kept) =>
        new(Path, Text, LineStarts, Lines, kept);

    /// <summary>
    /// Returns everything the 0-based line <paramref name="line"/> parsed to. That is its
    /// statement and the nodes and tokens the line holds outside the statement.
    /// </summary>
    internal Parser.Result Parsed(int line) => statements[line];

    /// <summary>
    /// Adds the diagnostics of <paramref name="green"/> and everything under it to
    /// <paramref name="result"/>, as spans in the file. <paramref name="position"/> is the offset
    /// where the node starts. Only subtrees whose flags show they hold a diagnostic are walked.
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
    /// Checks whether any line from <paramref name="first"/> to <paramref name="last"/>, both
    /// 0-based and inclusive, has a diagnostic on it. A line, a block and the file use this method
    /// to answer <see cref="SyntaxNode.ContainsDiagnostics"/>.
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
    /// Checks whether any line from <paramref name="first"/> to <paramref name="last"/>, both
    /// 0-based and inclusive, has an annotation. A line, a block and the file use this method to
    /// answer <see cref="SyntaxNode.ContainsAnnotations"/>.
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
    /// Adds the diagnostics of the lines from <paramref name="first"/> to <paramref name="last"/>,
    /// both 0-based and inclusive, to <paramref name="result"/> in source order.
    /// </summary>
    internal void CollectLines(int first, int last, List<Diagnostic> result) =>
        result.AddRange(CollectRange(first, last));

    /// <summary>
    /// Returns the offset where each line starts. <c>\r\n</c>, <c>\n</c> and a lone <c>\r</c> each
    /// end a line, as in LSP.
    /// </summary>
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

    /// <summary>
    /// Parses every line under <paramref name="node"/>, each in the kind of the block around it.
    /// </summary>
    private static void ParseLines(
        GreenNode node, BlockKind context, Parser.Result[] parsed, ImmutableArray<Parser.Result?> keptParses,
        ref int line)
    {
        for (var i = 0; i < node.SlotCount; i++)
        {
            // A block's opener and closer lines are inside it, so they are parsed with the
            // block's own kind as context; even a block whose lines have their own grammar has
            // its `}` line parsed the ordinary way.
            // A conditional or a repetition inside a data body holds values too, and a
            // conditional inside an enum holds members, so their lines are parsed the way the
            // enclosing body's own lines are.
            if (node.GetSlot(i) is GreenBlock block)
            {
                ParseLines(block, Within(context, block.BlockKind), parsed, keptParses, ref line);
                continue;
            }

            // A line that an annotating rewrite reattached annotations to keeps that parse, which
            // is the line's own parse with the annotations on it. A line whose enclosing block kind
            // has since changed is parsed again in its current context, and the annotations are
            // dropped along with the old parse.
            var at = line++;
            parsed[at] = !keptParses.IsDefaultOrEmpty && keptParses[at] is { } kept && kept.Context == context
                ? kept
                : ((GreenLine)node.GetSlot(i)!).Parse(context);
        }
    }

    /// <summary>
    /// Returns the block kind in which a block's lines are parsed. That is the block's own kind,
    /// unless the block is a conditional or a repetition inside a body with its own line grammar.
    /// Such a block's lines are parsed as that body's lines.
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
    /// Checks whether a parsed line has any diagnostic, either a lexical error on one of its
    /// tokens or a parse error on one of the nodes they parse to. A green line holds the tokens
    /// the lexer read and not those nodes, so both have to be checked.
    /// </summary>
    private static bool HasDiagnostics(Parser.Result parsed, GreenLine line) =>
        line.ContainsDiagnostics
        || parsed.Node.ContainsDiagnostics
        || parsed.ExportKeyword is { ContainsDiagnostics: true }
        || parsed.SkippedTokens is { ContainsDiagnostics: true };

    /// <summary>
    /// Checks whether anything on a parsed line has an annotation. As with the diagnostics, both
    /// the line's own tokens and what they parse to are checked.
    /// </summary>
    private static bool Marked(Parser.Result parsed, GreenLine line) =>
        line.ContainsAnnotations
        || parsed.Node.ContainsAnnotations
        || parsed.ExportKeyword is { ContainsAnnotations: true }
        || parsed.SkippedTokens is { ContainsAnnotations: true };

    /// <summary>
    /// Returns every diagnostic on the lines from <paramref name="first"/> to
    /// <paramref name="last"/>, ordered by line and column. Lines with no diagnostics are skipped,
    /// so collecting the whole file's diagnostics walks only the subtrees that contain one.
    /// </summary>
    private List<Diagnostic> CollectRange(int first, int last)
    {
        var result = new List<Diagnostic>();
        for (var i = first; i <= last; i++)
        {
            if (!reported[i])
                continue;

            // The green line is not walked, because its tokens also belong to the parsed nodes.
            // Together those nodes hold every token of the line exactly once, and the statement
            // also holds the missing tokens and the nodes that carry the parser's diagnostics.
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

        // A block error concerns the brace structure rather than anything inside a line, so it is
        // recorded as a line and a token index on it, and converted to a span here.
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
