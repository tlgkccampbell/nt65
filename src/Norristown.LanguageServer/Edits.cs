using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Provides the building blocks of code actions. These helpers find where a line starts and
/// ends, how deep it is indented and where a new line goes, and they rename a name everywhere it
/// is used. Nothing here knows what a change is for; each helper only produces text at a place
/// in a file.
/// </summary>
internal static class Edits
{
    /// <summary>
    /// The amount by which a body is indented deeper than the line that opens it, used when the
    /// file gives nothing to copy.
    /// </summary>
    public const string Indent = "    ";

    /// <summary>Converts the location a diagnostic is reported at to a span of its file.</summary>
    public static TextSpan SpanOf(SyntaxTree tree, Span span)
    {
        var line = Math.Clamp(span.LineIndex, 0, tree.LineStarts.Length - 1);
        var start = Math.Clamp(tree.LineStarts[line] + span.StartColumn - 1, 0, tree.Text.Length);
        return new TextSpan(start, Math.Clamp(span.EndColumn - span.StartColumn, 0, tree.Text.Length - start));
    }

    /// <summary>
    /// Returns an edit that inserts a new line after <paramref name="line"/>, or at the top of
    /// the file for line -1.
    /// </summary>
    public static Edit InsertAfter(SyntaxTree tree, int line, string text)
    {
        if (line + 1 < tree.LineStarts.Length)
            return new Edit(tree, new TextSpan(tree.LineStarts[line + 1], 0), text + "\n");

        // The last line has no line break after it to put the new line behind.
        return new Edit(tree, new TextSpan(tree.Text.Length, 0), (tree.Text.EndsWith('\n') ? "" : "\n") + text + "\n");
    }

    /// <summary>
    /// Returns an edit that inserts a new line before <paramref name="line"/>, indented as that
    /// line is.
    /// </summary>
    public static Edit InsertBefore(SyntaxTree tree, int line, string text) =>
        new(tree, new TextSpan(tree.LineStarts[line], 0), $"{IndentOf(tree, line)}{text}\n");

    /// <summary>
    /// Returns an edit that removes lines <paramref name="first"/> to <paramref name="last"/>
    /// whole.
    /// </summary>
    public static Edit RemoveLines(SyntaxTree tree, int first, int last)
    {
        var start = tree.LineStarts[first];
        return new Edit(tree, new TextSpan(start, tree.GetLineEnd(last) - start), "");
    }

    /// <summary>Returns the whitespace a line starts with.</summary>
    public static string IndentOf(SyntaxTree tree, int line)
    {
        var start = tree.LineStarts[line];
        var at = start;
        while (at < tree.Text.Length && tree.Text[at] is ' ' or '\t')
            at++;
        return tree.Text[start..at];
    }

    /// <summary>
    /// Returns the indentation for lines in the block <paramref name="line"/> opens. That is the
    /// indentation of the next line with code when it is deeper, and otherwise this line's
    /// indentation plus one level.
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

    /// <summary>
    /// Returns the last top-level line whose statement is a <typeparamref name="T"/>, or -1 if
    /// there is none.
    /// </summary>
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

    /// <summary>
    /// Returns the line on which the block that <paramref name="line"/> opens ends, or
    /// <paramref name="line"/> itself for a line that opens no block.
    /// </summary>
    public static int BlockEnd(SyntaxTree tree, int line) =>
        BlockOpenedBy(tree, line) is { } block ? tree.GetLineIndex(block.FullSpan.End - 1) : line;

    /// <summary>
    /// Returns the span of the block <paramref name="line"/> opens, or null for a line that opens
    /// no block.
    /// </summary>
    public static TextSpan? BodyOf(SyntaxTree tree, int line) => BlockOpenedBy(tree, line)?.FullSpan;

    /// <summary>
    /// Returns the innermost block that holds <paramref name="line"/>, or null at a file's top
    /// level.
    /// </summary>
    public static BlockSyntax? BlockAround(SyntaxTree tree, int line)
    {
        // A line that opens a block belongs to the block around it, not to the one it opens.
        var block = tree.GetLine(line).Parent as BlockSyntax;
        return block is not null && block.LineIndex == line ? block.Parent as BlockSyntax : block;
    }

    /// <summary>
    /// Returns the block <paramref name="line"/> opens, or null for a line that opens none. A
    /// block's opening line is the first line in it, so the line's parent is the block.
    /// </summary>
    public static BlockSyntax? BlockOpenedBy(SyntaxTree tree, int line) =>
        tree.GetLine(line).Parent is BlockSyntax block && block.LineIndex == line ? block : null;

    /// <summary>
    /// Returns the statement parsed from <paramref name="line"/>, or null when the file has no such
    /// line.
    /// </summary>
    public static StatementSyntax? StatementOn(SyntaxTree tree, int line) =>
        line >= 0 && line < tree.LineCount ? tree.GetLine(line).Statement : null;

    /// <summary>
    /// Returns the symbol declared on <paramref name="line"/>, or null for a line that declares
    /// none.
    /// </summary>
    public static Symbol? DeclaredOn(SemanticModel model, int line) =>
        model.Symbols.FirstOrDefault(symbol => symbol.Tree == model.Tree && symbol.DeclarationSpan.LineIndex == line);

    /// <summary>
    /// Returns the edits that rename <paramref name="symbol"/> to <paramref name="name"/>
    /// everywhere the program refers to it. An alias that another file gave it with
    /// <c>.use ... as</c> is left alone, so that file goes on reaching the symbol through its
    /// alias.
    /// </summary>
    public static IReadOnlyList<Edit> Rename(ProgramModel program, Symbol symbol, string name) =>
        [.. program.ReferencesTo(symbol)
            .Where(found => !found.Reference.IsAlias)
            .Select(found => new Edit(found.File.Tree, found.Reference.Span, name))];

    /// <summary>
    /// Checks whether a name follows a <c>::</c>, which makes it a step on a path rather than a
    /// name on its own.
    /// </summary>
    public static bool IsQualified(SyntaxTree tree, TextSpan span)
    {
        var at = span.Start - 1;
        while (at >= 0 && tree.Text[at] is ' ' or '\t')
            at--;
        return at >= 1 && tree.Text[at] == ':' && tree.Text[at - 1] == ':';
    }

    /// <summary>
    /// Formats a processor state as a signature or a <c>.state</c> gives it. Only the parts that
    /// are not unchanged are included, in the order the language uses.
    /// </summary>
    public static string FormatState(ProcessorState state) =>
        string.Join(", ", new[]
        {
            state.A == Width.Unchanged ? null : ProcessorState.Format(StateRegister.A, state.A),
            state.Index == Width.Unchanged ? null : ProcessorState.Format(StateRegister.Index, state.Index),
            state.E == ProcessorMode.Unchanged ? null : ProcessorState.Format(state.E),
            state.D.Kind == StateValueKind.Unchanged ? null : state.D.Format(StateRegister.DirectPage),
            state.B.Kind == StateValueKind.Unchanged ? null : state.B.Format(StateRegister.DataBank),
        }.OfType<string>());

    /// <summary>
    /// Returns the head of the routine or macro <paramref name="line"/> declares, which is the
    /// signature it already has and the position just before the <c>{</c> that opens its body.
    /// Returns null for a line that declares neither, and for a line whose <c>{</c> is missing
    /// from the source, since there is then no brace to insert before.
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
    /// Returns an edit that adds <paramref name="item"/> to the signature of the routine
    /// <paramref name="line"/> opens. The item goes after the items the signature already
    /// declares, or into a new signature if the routine has none. The item belongs to the entry
    /// state, so it goes at the end of the entry part, before any <c>-&gt;</c>. Returns null for
    /// a line that declares no routine.
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
    /// Returns an edit that adds <paramref name="register"/> to the <c>reads</c> item in the
    /// signature of the routine <paramref name="line"/> opens. It replaces <c>none</c>, or follows
    /// the registers the item already names. Returns null where the signature writes no
    /// <c>reads</c> of its own.
    /// </summary>
    public static Edit? ReadsItem(SyntaxTree tree, int line, string register)
    {
        if (RoutineHead(tree, line) is not ({ } signature, _)
            || signature.Entry.DescendantNodes().OfType<StateRegistersItemSyntax>()
                .FirstOrDefault(item => item.Name.Text.Equals("reads", StringComparison.OrdinalIgnoreCase)) is not { } reads
            || reads.Registers.Count == 0)
        {
            return null;
        }
        var last = reads.Registers[^1];
        return last.Name.Text.Equals("none", StringComparison.OrdinalIgnoreCase)
            ? new Edit(tree, last.Span, register)
            : new Edit(tree, new TextSpan(last.Span.End, 0), $", {register}");
    }

    /// <summary>
    /// Returns <paramref name="wanted"/> if nothing in <paramref name="model"/>'s file declares
    /// it, and otherwise <paramref name="wanted"/> followed by the smallest number from 2 up
    /// that nothing declares. Cheap locals live in a namespace of their own and clash with
    /// nothing, so their names are ignored.
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
