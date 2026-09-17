using Norristown.Project;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Lines of a routine lifted into a routine of their own, with a call left where they were.
/// <para>
/// The selection has to be code that a call can stand for: whole lines of one routine, with
/// nothing in them that leaves the routine, no name that only the routine around them declares,
/// and nothing outside them naming a label they declare. Where any of that does not hold, the
/// lines are not offered for extraction rather than extracted wrongly.
/// </para>
/// <para>
/// On the 65816 the new routine declares the state the analysis finds where the lines were: the
/// state reaching the first of them, and the state reaching whatever followed the last, which
/// is what the lines leave. A routine that says nothing about the state would be read as
/// assuming the defaults, which is not what the code was written under.
/// </para>
/// </summary>
internal static class ExtractProc
{
    /// <summary>The instructions a selection ends a path with, which a call cannot stand for.</summary>
    private static readonly HashSet<string> leaves =
        new(StringComparer.OrdinalIgnoreCase) { "rts", "rtl", "rti", "jmp", "jml", "brk" };

    /// <summary>The lines <paramref name="range"/> covers, as a routine of their own, where they may be one.</summary>
    public static IEnumerable<Change> In(ProgramAnalysis analysis, SemanticModel model, Protocol.Range range)
    {
        var tree = model.Tree;
        var first = Math.Clamp(range.Start.Line, 0, tree.LineStarts.Length - 1);

        // A selection that ends where a line starts covers the lines above it, which is how an
        // editor spells a selection made by dragging down the left of them.
        var last = range.End.Line > first && range.End.Character == 0 ? range.End.Line - 1 : range.End.Line;
        last = Math.Clamp(last, first, tree.LineStarts.Length - 1);
        if (Selected(tree, first, last) is not { } lines)
            yield break;
        if (Around(tree, first, last) is not { } block || DeclaredOn(model, block.LineIndex) is not { Kind: SymbolKind.Proc } routine)
            yield break;
        if (!IsSelfContained(model, tree, lines, first, last))
            yield break;

        var name = Edits.UnusedName(model, Called(tree, first) ?? "extracted");
        var indent = Edits.IndentOf(tree, block.LineIndex);
        var body = Edits.BodyIndent(tree, block.LineIndex);
        var written = string.Join("\n", lines.Select(line => Written(tree, line)));
        var signature = Signature(analysis, model, lines, last);
        var declaration = $"\n{indent}.proc {name}{signature} {{\n{written}\n{body}rts\n{indent}}}\n";

        // The call stands where the lines did, indented as they were — unless they started
        // with a label at the margin, which is no indent for an instruction to take.
        var call = Edits.IndentOf(tree, first) is { Length: > 0 } own ? own : body;
        var declared = Edits.InsertAfter(tree, Edits.BlockEnd(tree, block.LineIndex), declaration.TrimEnd('\n'));
        var edits = new List<Edit>
        {
            Edits.RemoveLines(tree, first, last) with { Text = $"{call}jsr {name}\n" },
            declared,
        };

        // What the routine is called is the programmer's to say, so the editor is asked to
        // start a rename on the name it was given to be going on with.
        var at = declared.Text.IndexOf($".proc {name}", StringComparison.Ordinal) + ".proc ".Length;
        yield return new Change("Extract into a `.proc`", CodeActionKinds.Extract, edits,
            Names: new Change.Placeholder(declared, at));
    }

    /// <summary>
    /// What to call the routine before the programmer says: the label the selection starts
    /// with, which is the one word about these lines that the file already has, and null where
    /// it starts with none.
    /// </summary>
    private static string? Called(SyntaxTree tree, int first) =>
        StatementOn(tree, first) is { Kind: SyntaxKind.LabeledLine } labelled
            && labelled.ChildNodes.FirstOrDefault() is { Kind: SyntaxKind.Label } label
            && label.ChildTokens is [var name, ..]
            ? name.Text.TrimStart('@')
            : null;

    /// <summary>
    /// The lines of the selection, where every one of them is code a call can stand for: an
    /// instruction, a label, or a line with nothing on it. Null for a selection holding
    /// anything else, or one that leaves the routine part way through.
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
            var kinds = statement.Kind == SyntaxKind.LabeledLine
                ? statement.ChildNodes.Select(child => child.Kind).ToList()
                : [statement.Kind];
            foreach (var kind in kinds)
            {
                if (kind is not (SyntaxKind.InstructionStatement or SyntaxKind.Label or SyntaxKind.BlankLine))
                    return null;
                code |= kind == SyntaxKind.InstructionStatement;
            }
            if (Mnemonic(statement) is { } mnemonic && leaves.Contains(mnemonic))
                return null;
            lines.Add(line);
        }
        return code ? lines : null;
    }

    /// <summary>The block holding the whole selection, where one routine's body holds all of it.</summary>
    private static SyntaxNode? Around(SyntaxTree tree, int first, int last)
    {
        var block = Edits.BlockAround(tree, first);
        return block is { Green: GreenBlock { BlockKind: BlockKind.Proc } } && block == Edits.BlockAround(tree, last)
            ? block
            : null;
    }

    /// <summary>
    /// Whether the selection stands on its own: nothing in it names a label of the routine
    /// around it, and nothing outside it names a label declared in it.
    /// </summary>
    private static bool IsSelfContained(
        SemanticModel model, SyntaxTree tree, IReadOnlyList<int> lines, int first, int last)
    {
        var from = tree.LineStarts[first];
        var to = last + 1 < tree.LineStarts.Length ? tree.LineStarts[last + 1] : tree.Text.Length;
        foreach (var reference in model.References)
        {
            var inside = reference.Span.Start >= from && reference.Span.End <= to;
            if (reference.Symbol is not { Kind: SymbolKind.Label } label)
                continue;

            // A label declared in the selection travels with it, and one outside it stays
            // where it is: either way, a name written on the wrong side of the line is what
            // says the lines cannot be lifted out on their own.
            var declared = label.NameSpan.Start >= from && label.NameSpan.End <= to;
            if (inside != declared && label.Routine is not null)
                return false;
        }
        return true;
    }

    /// <summary>
    /// What the new routine declares: the state reaching the first line and, where it differs,
    /// the state the lines leave, which is what reached whatever followed them. Nothing at all
    /// on the processors that have no state to track.
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
    /// A line as it was written. The new routine stands where the old one does, so its body is
    /// indented the way that one's was, and a label at the margin stays at the margin.
    /// </summary>
    private static string Written(SyntaxTree tree, int line) =>
        tree.Text[tree.LineStarts[line]..LineEnd(tree, line)].TrimEnd();

    /// <summary>The instruction statements of the selected lines, in order.</summary>
    private static IEnumerable<SyntaxNode> Statements(SyntaxTree tree, IReadOnlyList<int> lines)
    {
        foreach (var line in lines)
        {
            if (Instruction(StatementOn(tree, line)) is { } statement)
                yield return statement;
        }
    }

    /// <summary>The first instruction after <paramref name="line"/> in the same block, or null.</summary>
    private static SyntaxNode? StatementAfter(SyntaxTree tree, int line)
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
    private static SyntaxNode? Instruction(SyntaxNode? statement) => statement switch
    {
        { Kind: SyntaxKind.InstructionStatement } => statement,
        { Kind: SyntaxKind.LabeledLine } => statement.ChildNodes
            .FirstOrDefault(child => child.Kind == SyntaxKind.InstructionStatement),
        _ => null,
    };

    /// <summary>The mnemonic a line's instruction is written with, or null for a line with none.</summary>
    private static string? Mnemonic(SyntaxNode statement) =>
        Instruction(statement) is { ChildTokens: [var mnemonic, ..] } ? mnemonic.Text : null;

    /// <summary>The symbol declared on <paramref name="line"/>, or null for a line that declares none.</summary>
    private static Symbol? DeclaredOn(SemanticModel model, int line) =>
        model.Symbols.FirstOrDefault(symbol => symbol.Tree == model.Tree && symbol.DeclarationSpan.Line - 1 == line);

    /// <summary>The statement parsed from <paramref name="line"/>, or null where the file has no such line.</summary>
    private static SyntaxNode? StatementOn(SyntaxTree tree, int line)
    {
        foreach (var node in tree.Root.DescendantNodes())
        {
            if (node.Green is GreenLine && node.LineIndex == line)
                return node.Statement;
        }
        return null;
    }

    /// <summary>Where a line's text ends, the line break included.</summary>
    private static int LineEnd(SyntaxTree tree, int line) =>
        line + 1 < tree.LineStarts.Length ? tree.LineStarts[line + 1] : tree.Text.Length;
}
