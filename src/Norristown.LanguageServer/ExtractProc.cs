using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Provides the refactoring that moves lines of a routine into a new routine of their own, leaving
/// a call in their place.
/// <para>
/// The selection must be code that a call can replace. It must consist of whole lines of one
/// routine, with no instruction that leaves the routine, no reference to a label the enclosing
/// routine declares outside the selection, and no reference from outside to a label declared in
/// the selection. Where any of these conditions fails, extraction is not offered, rather than
/// done wrongly.
/// </para>
/// <para>
/// On the 65816 the new routine's signature declares the processor state the analysis finds at
/// the lines. The entry state is the state on entry to the first line, and the exit state is the
/// state on entry to whatever followed the last line. A routine that declares no state would be
/// read as assuming the default state, which is not what the code was written for.
/// </para>
/// </summary>
internal static class ExtractProc
{
    /// <summary>
    /// Returns the change that extracts the lines <paramref name="range"/> covers into a routine,
    /// or nothing where that is not possible.
    /// </summary>
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
        var moved = string.Join("\n", lines.Select(line => LineText(tree, line)));
        var signature = Signature(analysis, model, lines, last);
        var declaration = $"\n{indent}.proc {name}{signature} {{\n{moved}\n{body}rts\n{indent}}}\n";

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
    /// Returns the placeholder name for the new routine, which is the label the selection starts
    /// with, or null where it starts with none. That label is the only name the file already
    /// gives these lines.
    /// </summary>
    private static string? Called(SyntaxTree tree, int first) =>
        StatementOn(tree, first) is LabeledLineSyntax labelled ? labelled.Label.Name.Text.TrimStart('@') : null;

    /// <summary>
    /// Returns the lines of the selection, provided each is code a call can replace, which means
    /// an instruction, a label or a blank line. Returns null for a selection that holds anything
    /// else, for one that contains a return, which would return from the new routine instead,
    /// and for one with no instruction at all.
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
                if (Instructions.Facts(instruction.MnemonicKind).Control == Control.Returns)
                    return null;
                code = true;
            }
            lines.Add(line);
        }
        return code ? lines : null;
    }

    /// <summary>
    /// Returns the block holding the whole selection, or null unless one routine's body holds all
    /// of it.
    /// </summary>
    private static BlockSyntax? Around(SyntaxTree tree, int first, int last)
    {
        var block = Edits.BlockAround(tree, first);
        return block is { BlockKind: BlockKind.Proc } && block == Edits.BlockAround(tree, last)
            ? block
            : null;
    }

    /// <summary>
    /// Checks whether the selection is self-contained. Nothing in it may name a label the enclosing
    /// routine declares outside it, nothing outside it may name a label declared in it, and every
    /// jump in it must land on a label declared in it. A jump anywhere else — to another routine,
    /// or through a pointer — would leave the new routine without coming back to its caller.
    /// </summary>
    private static bool IsSelfContained(SemanticModel model, SyntaxTree tree, int first, int last)
    {
        var from = tree.LineStarts[first];
        var to = last + 1 < tree.LineStarts.Length ? tree.LineStarts[last + 1] : tree.Text.Length;
        bool Within(int start, int end) => start >= from && end <= to;

        for (var line = first; line <= last; line++)
        {
            if (Instruction(StatementOn(tree, line)) is not { } jump
                || Instructions.Facts(jump.MnemonicKind).Control != Control.Jumps)
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
    /// Returns the new routine's signature, which gives the state on entry to the first line and,
    /// where it differs, the state the lines exit with. The exit state is the state on entry to
    /// whatever followed the lines. The signature is empty on processors that have no such state
    /// to track.
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

        var items = Edits.FormatState(entry);
        if (items.Length == 0)
            return "";
        var after = StatementAfter(model.Tree, last);
        var exit = after is not null ? states.AnyBefore(after)?.Processor : null;
        return exit is { } left && left != entry && Edits.FormatState(left) is { Length: > 0 } leaving
            ? $": {items} -> {leaving}"
            : $": {items}";
    }

    /// <summary>
    /// Returns a line's text unchanged apart from trailing whitespace. The new routine is declared
    /// at the same level as the old one, so its body keeps the old body's indentation, and a label
    /// at the margin stays there.
    /// </summary>
    private static string LineText(SyntaxTree tree, int line) =>
        tree.Text[tree.LineStarts[line]..LineEnd(tree, line)].TrimEnd();

    /// <summary>Returns the instruction statements of the selected lines, in order.</summary>
    private static IEnumerable<InstructionStatementSyntax> Statements(SyntaxTree tree, IReadOnlyList<int> lines)
    {
        foreach (var line in lines)
        {
            if (Instruction(StatementOn(tree, line)) is { } statement)
                yield return statement;
        }
    }

    /// <summary>
    /// Returns the first instruction after <paramref name="line"/> in the same block, or null if
    /// there is none.
    /// </summary>
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

    /// <summary>
    /// Returns the instruction a statement holds, including one after a label, or null for a
    /// statement that holds none.
    /// </summary>
    private static InstructionStatementSyntax? Instruction(StatementSyntax? statement) => statement switch
    {
        InstructionStatementSyntax instruction => instruction,
        LabeledLineSyntax labelled => labelled.Statement as InstructionStatementSyntax,
        _ => null,
    };

    /// <summary>
    /// Returns the symbol declared on <paramref name="line"/>, or null for a line that declares
    /// none.
    /// </summary>
    private static Symbol? DeclaredOn(SemanticModel model, int line) =>
        model.Symbols.FirstOrDefault(symbol => symbol.Tree == model.Tree && symbol.DeclarationSpan.Line - 1 == line);

    /// <summary>
    /// Returns the statement parsed from <paramref name="line"/>, or null where the file has no
    /// such line.
    /// </summary>
    private static StatementSyntax? StatementOn(SyntaxTree tree, int line) =>
        line >= 0 && line < tree.LineCount ? tree.GetLine(line).Statement : null;

    /// <summary>Returns the position where a line's text ends, including the line break.</summary>
    private static int LineEnd(SyntaxTree tree, int line) =>
        line + 1 < tree.LineStarts.Length ? tree.LineStarts[line + 1] : tree.Text.Length;
}
