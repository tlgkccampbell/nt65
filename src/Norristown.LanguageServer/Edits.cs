using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// The building blocks of code actions: where a line starts and ends, how deep it is indented,
/// where a new line goes, and renaming a name everywhere it is written. Nothing here knows what
/// a change is for; it only writes text at a place in a file.
/// </summary>
internal static class Edits
{
    /// <summary>How much deeper a body is indented than the line that opens it, when the file gives nothing to copy.</summary>
    public const string Indent = "    ";

    /// <summary>The text a diagnostic is reported on, as a span of its file.</summary>
    public static TextSpan SpanOf(SyntaxTree tree, Span span)
    {
        var line = Math.Clamp(span.Line - 1, 0, tree.LineStarts.Length - 1);
        var start = Math.Clamp(tree.LineStarts[line] + span.StartColumn - 1, 0, tree.Text.Length);
        return new TextSpan(start, Math.Clamp(span.EndColumn - span.StartColumn, 0, tree.Text.Length - start));
    }

    /// <summary>A new line after <paramref name="line"/>, or at the top of the file for line -1.</summary>
    public static Edit InsertAfter(SyntaxTree tree, int line, string text)
    {
        if (line + 1 < tree.LineStarts.Length)
            return new Edit(tree, new TextSpan(tree.LineStarts[line + 1], 0), text + "\n");

        // The last line has no line break after it to put the new line behind.
        return new Edit(tree, new TextSpan(tree.Text.Length, 0), (tree.Text.EndsWith('\n') ? "" : "\n") + text + "\n");
    }

    /// <summary>A new line before <paramref name="line"/>, indented as that line is.</summary>
    public static Edit InsertBefore(SyntaxTree tree, int line, string text) =>
        new(tree, new TextSpan(tree.LineStarts[line], 0), $"{IndentOf(tree, line)}{text}\n");

    /// <summary>Lines <paramref name="first"/> to <paramref name="last"/>, taken out whole.</summary>
    public static Edit RemoveLines(SyntaxTree tree, int first, int last)
    {
        var start = tree.LineStarts[first];
        var end = last + 1 < tree.LineStarts.Length ? tree.LineStarts[last + 1] : tree.Text.Length;
        return new Edit(tree, new TextSpan(start, end - start), "");
    }

    /// <summary>The whitespace a line starts with.</summary>
    public static string IndentOf(SyntaxTree tree, int line)
    {
        var start = tree.LineStarts[line];
        var at = start;
        while (at < tree.Text.Length && tree.Text[at] is ' ' or '\t')
            at++;
        return tree.Text[start..at];
    }

    /// <summary>
    /// The indentation for lines in the block <paramref name="line"/> opens: that of the next
    /// line with code when it is deeper, or else this line's indentation plus one level.
    /// </summary>
    public static string BodyIndent(SyntaxTree tree, int line)
    {
        var own = IndentOf(tree, line);
        for (var next = line + 1; next < tree.LineStarts.Length; next++)
        {
            if (LineContext.TokensOf(tree, next).Count == 0)
                continue;
            var indent = IndentOf(tree, next);
            return indent.Length > own.Length ? indent : own + Indent;
        }
        return own + Indent;
    }

    /// <summary>The last line at the top level whose statement is a <typeparamref name="T"/>, or -1.</summary>
    public static int LastLine<T>(SyntaxTree tree) where T : StatementSyntax
    {
        var found = -1;
        foreach (var child in tree.Root.Members)
        {
            if (child is LineSyntax { Statement: T } line)
                found = line.LineIndex;
        }
        return found;
    }

    /// <summary>The line a block ends on, for the line that opens it; that same line for one that opens none.</summary>
    public static int BlockEnd(SyntaxTree tree, int line) =>
        BlockOpenedBy(tree, line) is { } block ? tree.GetLineIndex(block.FullSpan.End - 1) : line;

    /// <summary>The block <paramref name="line"/> opens, as a range of the file; null for a line that opens none.</summary>
    public static TextSpan? BodyOf(SyntaxTree tree, int line) => BlockOpenedBy(tree, line)?.FullSpan;

    /// <summary>The innermost block that holds <paramref name="line"/>, or null at a file's top level.</summary>
    public static BlockSyntax? BlockAround(SyntaxTree tree, int line)
    {
        // A line that opens a block is written in the block around it, not in the one it opens.
        var block = tree.GetLine(line).Parent as BlockSyntax;
        return block is not null && block.LineIndex == line ? block.Parent as BlockSyntax : block;
    }

    /// <summary>
    /// The block <paramref name="line"/> opens, or null for a line that opens none. A block's
    /// opening line is the first line in it, so the line's parent is the block.
    /// </summary>
    public static BlockSyntax? BlockOpenedBy(SyntaxTree tree, int line) =>
        tree.GetLine(line).Parent is BlockSyntax block && block.LineIndex == line ? block : null;

    /// <summary>
    /// The edits that rename <paramref name="symbol"/> to <paramref name="name"/> everywhere the
    /// program writes it. An alias another file gave it with <c>.use ... as</c> is left alone:
    /// that file goes on reaching the symbol through its alias.
    /// </summary>
    public static IReadOnlyList<Edit> Rename(ProgramModel program, Symbol symbol, string name) =>
        [.. program.ReferencesTo(symbol)
            .Where(found => !found.Reference.IsAlias)
            .Select(found => new Edit(found.File.Tree, found.Reference.Span, name))];

    /// <summary>Whether a name is written after a <c>::</c>, which makes it a step on a path rather than a name on its own.</summary>
    public static bool IsQualified(SyntaxTree tree, TextSpan span)
    {
        var at = span.Start - 1;
        while (at >= 0 && tree.Text[at] is ' ' or '\t')
            at--;
        return at >= 1 && tree.Text[at] == ':' && tree.Text[at - 1] == ':';
    }

    /// <summary>
    /// A processor state as a signature or a <c>.state</c> writes it: only the parts that are
    /// not unchanged, in the order the language writes them.
    /// </summary>
    public static string SpellState(ProcessorState state) =>
        string.Join(", ", new[]
        {
            state.A == Width.Unchanged ? null : ProcessorState.Spell("a", state.A),
            state.Index == Width.Unchanged ? null : ProcessorState.Spell("i", state.Index),
            state.E == ProcessorMode.Unchanged ? null : ProcessorState.Spell(state.E),
            state.D.Kind == StateValueKind.Unchanged ? null : state.D.Spell("dp"),
            state.B.Kind == StateValueKind.Unchanged ? null : state.B.Spell("dbr"),
        }.OfType<string>());

    /// <summary>
    /// The head of the routine or macro <paramref name="line"/> declares: the signature it
    /// already has, and the position just before the <c>{</c> that opens its body. Null for a
    /// line that declares neither, and for one whose <c>{</c> is missing from the source, since
    /// there is then no brace to insert before.
    /// </summary>
    public static (ProcSignatureSyntax? Signature, int BeforeBrace)? RoutineHead(SyntaxTree tree, int line)
    {
        var (signature, brace) = (line >= 0 && line < tree.LineCount ? tree.GetLine(line).Statement : null) switch
        {
            ProcDeclarationSyntax proc => (proc.Signature, (SyntaxToken?)proc.OpenBraceToken),
            MultiProcDeclarationSyntax multi => (multi.Signature, multi.OpenBraceToken),
            MacroDeclarationSyntax macro => (macro.Signature, macro.OpenBraceToken),
            _ => (null, null),
        };
        return brace is not { IsMissing: false } opened
            ? null
            : (signature, opened.GetPreviousToken()?.Span.End ?? opened.Span.Start);
    }

    /// <summary>
    /// <paramref name="item"/> written into the signature of the routine <paramref name="line"/>
    /// opens: after the items it already declares, or as a new signature if it has none. The
    /// item belongs to the entry state, so it goes at the end of the entry part, before any
    /// <c>-&gt;</c>. Null for a line that declares no routine.
    /// </summary>
    public static Edit? SignatureItem(SyntaxTree tree, int line, string item)
    {
        if (RoutineHead(tree, line) is not var (signature, beforeBrace))
            return null;
        return signature is null
            ? new Edit(tree, new TextSpan(beforeBrace, 0), $": {item}")
            : new Edit(tree, new TextSpan(signature.Entry.Span.End, 0), $", {item}");
    }

    /// <summary>
    /// <paramref name="wanted"/>, or <paramref name="wanted"/> followed by the smallest number
    /// from 2 up, whichever nothing in <paramref name="model"/>'s file declares. Cheap locals
    /// live in a namespace of their own and clash with nothing, so their names are ignored.
    /// </summary>
    public static string UnusedName(SemanticModel model, string wanted)
    {
        var taken = model.Symbols
            .Where(symbol => !symbol.IsCheapLocal)
            .Select(symbol => symbol.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (!taken.Contains(wanted))
            return wanted;
        for (var n = 2; ; n++)
        {
            if (!taken.Contains($"{wanted}{n}"))
                return $"{wanted}{n}";
        }
    }
}
