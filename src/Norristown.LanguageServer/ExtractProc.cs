using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// The refactoring that moves lines of a routine into a new routine of their own, leaving a
/// call in their place.
/// <para>
/// The selection must be code a call can replace: whole lines of one routine, with no
/// instruction that leaves the routine, no reference to a label the enclosing routine declares
/// outside the selection, and no reference from outside to a label declared in the selection.
/// Where any of that does not hold, extraction is not offered, rather than done wrongly.
/// </para>
/// <para>
/// On the 65816 the new routine's signature declares the processor state the analysis finds at
/// the lines: the state on entry to the first, and, as the exit state, the state on entry to
/// whatever followed the last. A routine that states nothing would be read as assuming the
/// default state, which is not what the code was written for.
/// </para>
/// </summary>
internal static class ExtractProc
{
    /// <summary>The change that extracts the lines <paramref name="range"/> covers into a routine, where that is possible.</summary>
    public static IEnumerable<Change> In(ProgramAnalysis analysis, SemanticModel model, Protocol.Range range)
    {
        var tree = model.Tree;
        var first = Math.Clamp(range.Start.Line, 0, tree.LineStarts.Length - 1);

        // A selection ending at the start of a line covers only the lines above it; that is how
        // an editor represents whole lines selected by dragging down the margin.
        var last = range.End.Line > first && range.End.Character == 0 ? range.End.Line - 1 : range.End.Line;
        last = Math.Clamp(last, first, tree.LineStarts.Length - 1);
        if (Selected(tree, first, last) is not { } lines)
            yield break;
        if (Around(tree, first, last) is not { } block || DeclaredOn(model, block.LineIndex) is not { Kind: SymbolKind.Proc } routine)
            yield break;
        if (!IsSelfContained(model, tree, first, last))
            yield break;

        var name = Edits.UnusedName(model, Called(tree, first) ?? "extracted");
        var indent = Edits.IndentOf(tree, block.LineIndex);
        var body = Edits.BodyIndent(tree, block.LineIndex);
        var written = string.Join("\n", lines.Select(line => Written(tree, line)));
        var signature = Signature(analysis, model, lines, last);
        var declaration = $"\n{indent}.proc {name}{signature} {{\n{written}\n{body}rts\n{indent}}}\n";

        // The call goes where the lines were, indented as they were, unless they started with a
        // label at the left margin, which is no indentation for an instruction; then the call
        // takes the body's indentation.
        var call = Edits.IndentOf(tree, first) is { Length: > 0 } own ? own : body;
        var declared = Edits.InsertAfter(tree, Edits.BlockEnd(tree, block.LineIndex), declaration.TrimEnd('\n'));
        var edits = new List<Edit>
        {
            Edits.RemoveLines(tree, first, last) with { Text = $"{call}jsr {name}\n" },
            declared,
        };

        // Only the programmer can choose the routine's name, so the editor is asked to start a
        // rename on the placeholder name.
        var at = declared.Text.IndexOf($".proc {name}", StringComparison.Ordinal) + ".proc ".Length;
        yield return new Change("Extract into a `.proc`", CodeActionKinds.Extract, edits,
            Names: new Change.Placeholder(declared, at));
    }

    /// <summary>
    /// The placeholder name for the new routine: the label the selection starts with, which is
    /// the only name the file already gives these lines, or null where it starts with none.
    /// </summary>
    private static string? Called(SyntaxTree tree, int first) =>
        StatementOn(tree, first) is LabeledLineSyntax labelled ? labelled.Label.Name.Text.TrimStart('@') : null;

    /// <summary>
    /// The lines of the selection, provided each is code a call can replace: an instruction, a
    /// label, or a blank line. Null for a selection holding anything else, one containing a
    /// return, which would return from the new routine instead, or one with no instruction at all.
    /// </summary>
    private static IReadOnlyList<int>? Selected(SyntaxTree tree, int first, int last)
    {
        var lines = new List<int>();
        var code = false;
        for (var line = first; line <= last; line++)
        {
            var statement = StatementOn(tree, line);
            if (statement is null)
                return null;
            if (statement is not (InstructionStatementSyntax or BlankLineSyntax
                or LabeledLineSyntax { Statement: null or InstructionStatementSyntax }))
            {
                return null;
            }
            if (Instruction(statement) is { } instruction)
            {
                if (Instructions.Facts(instruction.Mnemonic.Text).Control == Control.Returns)
                    return null;
                code = true;
            }
            lines.Add(line);
        }
        return code ? lines : null;
    }

    /// <summary>The block holding the whole selection, where one routine's body holds all of it.</summary>
    private static BlockSyntax? Around(SyntaxTree tree, int first, int last)
    {
        var block = Edits.BlockAround(tree, first);
        return block is { BlockKind: BlockKind.Proc } && block == Edits.BlockAround(tree, last)
            ? block
            : null;
    }

    /// <summary>
    /// Whether the selection is self-contained: nothing in it names a label the enclosing
    /// routine declares outside it, nothing outside it names a label declared in it, and every
    /// jump in it lands on a label declared in it. A jump anywhere else — to another routine, or
    /// through a pointer — would leave the new routine without coming back to its caller.
    /// </summary>
    private static bool IsSelfContained(SemanticModel model, SyntaxTree tree, int first, int last)
    {
        var from = tree.LineStarts[first];
        var to = last + 1 < tree.LineStarts.Length ? tree.LineStarts[last + 1] : tree.Text.Length;
        bool Within(int start, int end) => start >= from && end <= to;

        for (var line = first; line <= last; line++)
        {
            if (Instruction(StatementOn(tree, line)) is not { } jump
                || Instructions.Facts(jump.Mnemonic.Text).Control != Control.Jumps)
            {
                continue;
            }
            var operand = jump.Operand?.Span;
            var lands = operand is { } span && model.References.Any(reference =>
                reference.Span.Start >= span.Start && reference.Span.End <= span.End
                && reference.Symbol is { Kind: SymbolKind.Label } label
                && Within(label.NameSpan.Start, label.NameSpan.End));
            if (!lands)
                return false;
        }

        foreach (var reference in model.References)
        {
            var inside = Within(reference.Span.Start, reference.Span.End);
            if (reference.Symbol is not { Kind: SymbolKind.Label } label)
                continue;

            // A label declared in the selection moves with it, and one declared outside stays
            // behind; a reference on the other side of that boundary from its label means the
            // lines cannot be extracted on their own.
            var declared = Within(label.NameSpan.Start, label.NameSpan.End);
            if (inside != declared && label.Routine is not null)
                return false;
        }
        return true;
    }

    /// <summary>
    /// The new routine's signature: the state on entry to the first line and, where it differs,
    /// the state the lines exit with, which is the state on entry to whatever followed them.
    /// Empty on processors that have no such state to track.
    /// </summary>
    private static string Signature(
        ProgramAnalysis analysis, SemanticModel model, IReadOnlyList<int> lines, int last)
    {
        if (analysis.Cpu != Cpu.Wdc65816 || analysis.StatesFor(model.Tree.Path) is not { } states)
            return "";
        if (Statements(model.Tree, lines).FirstOrDefault() is not { } start
            || states.AnyBefore(start)?.Processor is not { } entry)
        {
            return "";
        }

        var items = Edits.SpellState(entry);
        if (items.Length == 0)
            return "";
        var after = StatementAfter(model.Tree, last);
        var exit = after is not null ? states.AnyBefore(after)?.Processor : null;
        return exit is { } left && left != entry && Edits.SpellState(left) is { Length: > 0 } leaving
            ? $": {items} -> {leaving}"
            : $": {items}";
    }

    /// <summary>
    /// A line exactly as written. The new routine is declared at the same level as the old one,
    /// so its body keeps the old body's indentation, and a label at the margin stays there.
    /// </summary>
    private static string Written(SyntaxTree tree, int line) =>
        tree.Text[tree.LineStarts[line]..LineEnd(tree, line)].TrimEnd();

    /// <summary>The instruction statements of the selected lines, in order.</summary>
    private static IEnumerable<InstructionStatementSyntax> Statements(SyntaxTree tree, IReadOnlyList<int> lines)
    {
        foreach (var line in lines)
        {
            if (Instruction(StatementOn(tree, line)) is { } statement)
                yield return statement;
        }
    }

    /// <summary>The first instruction after <paramref name="line"/> in the same block, or null.</summary>
    private static InstructionStatementSyntax? StatementAfter(SyntaxTree tree, int line)
    {
        var block = Edits.BlockAround(tree, line);
        var end = block is not null ? tree.GetLineIndex(block.FullSpan.End - 1) : tree.LineStarts.Length - 1;
        for (var next = line + 1; next <= end; next++)
        {
            if (Instruction(StatementOn(tree, next)) is { } statement)
                return statement;
        }
        return null;
    }

    /// <summary>The instruction a line holds, a labelled one included; null for a line holding none.</summary>
    private static InstructionStatementSyntax? Instruction(StatementSyntax? statement) => statement switch
    {
        InstructionStatementSyntax instruction => instruction,
        LabeledLineSyntax labelled => labelled.Statement as InstructionStatementSyntax,
        _ => null,
    };

    /// <summary>The symbol declared on <paramref name="line"/>, or null for a line that declares none.</summary>
    private static Symbol? DeclaredOn(SemanticModel model, int line) =>
        model.Symbols.FirstOrDefault(symbol => symbol.Tree == model.Tree && symbol.DeclarationSpan.Line - 1 == line);

    /// <summary>The statement parsed from <paramref name="line"/>, or null where the file has no such line.</summary>
    private static StatementSyntax? StatementOn(SyntaxTree tree, int line) =>
        line >= 0 && line < tree.LineCount ? tree.GetLine(line).Statement : null;

    /// <summary>Where a line's text ends, the line break included.</summary>
    private static int LineEnd(SyntaxTree tree, int line) =>
        line + 1 < tree.LineStarts.Length ? tree.LineStarts[line + 1] : tree.Text.Length;
}
