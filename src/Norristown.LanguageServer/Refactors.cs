using Norristown.Project;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// The changes offered at a selection, which nothing reported: a name written the other way,
/// the <c>.use</c> items tidied, a declaration exported or not, what a routine leaves declared,
/// widths written as an <c>.ensure</c> or written out, a number given a name, a label given one
/// or made cheap, a declaration put in a segment of its own, code lifted into a routine, and
/// ca65 read as nt65.
/// <para>
/// Each of them is offered only where it would change something, and each is worked out from
/// the analysis rather than from the text alone, so a rewrite means what the line meant.
/// </para>
/// </summary>
internal static class Refactors
{
    /// <summary>The rewrites offered over <paramref name="range"/> of <paramref name="model"/>'s file.</summary>
    public static IReadOnlyList<Change> In(ProgramAnalysis analysis, SemanticModel model, Protocol.Range range)
    {
        var tree = model.Tree;
        if (tree.LineStarts.Length == 0)
            return [];

        // Most of them are about the line the caret is on; the two that lift code out are
        // about the lines it covers.
        var line = Math.Clamp(range.Start.Line, 0, tree.LineStarts.Length - 1);
        var caret = tree.GetPosition(range.Start.Line, range.Start.Character);
        return
        [
            .. Paths(model, caret),
            .. UseItems.Organized(model, line),
            .. Exported(model, line),
            .. Leaves(analysis, model, line),
            .. Widths(analysis, model, line),
            .. Named(model, caret, line),
            .. Labels(analysis.Program, model, caret),
            .. Segments(model, line),
            .. ExtractProc.In(analysis, model, range),
            .. Ca65Conversion.In(model, range),
        ];
    }

    /// <summary>
    /// A name another module declares, written the other way round: one written out in full is
    /// brought in with a <c>.use</c>, and one a <c>.use</c> brought in is written out in full,
    /// with the item that brought it taken away when nothing else needs it.
    /// </summary>
    private static IEnumerable<Change> Paths(SemanticModel model, int caret)
    {
        var tree = model.Tree;
        if (PathAt(model, caret) is not { } reference)
            yield break;
        if (reference.Symbol is not { IsDefine: false, IsConfig: false } symbol || symbol.Tree == tree)
            yield break;
        if (symbol.PathName is not { } path || !path.Contains("::", StringComparison.Ordinal))
            yield break;

        var name = tree.Text[reference.Span.Start..reference.Span.End];
        if (Edits.IsQualified(tree, reference.Span))
        {
            // Every writing of the path in this file is shortened, so that the file says the
            // name one way: what a `.use` brings in, it brings in for all of them.
            var edits = new List<Edit>();
            foreach (var other in model.ReferencesTo(symbol))
            {
                if (other is { IsDeclaration: false, InUse: false } && PathOf(tree, other.Span) is { } whole)
                    edits.Add(new Edit(tree, whole, symbol.Name));
            }
            if (edits.Count > 0)
            {
                edits.Add(Edits.InsertAfter(tree,
                    Edits.LastLine<UseDirectiveSyntax>(tree) is var use and >= 0
                        ? use
                        : Edits.LastLine<ModuleDirectiveSyntax>(tree),
                    $".use {path}"));
                yield return new Change($"Bring in `{path}` with `.use`", CodeActionKinds.Rewrite, edits);
            }
            yield break;
        }

        // The other way: the name is written out wherever this file writes it, and the item
        // that brought it in has nothing left to do. An alias is left alone, because the name
        // it gives is this file's own choice rather than a short way to write the path.
        if (reference.IsAlias || !model.Brought.TryGetValue(name, out var brought) || brought.Symbol != symbol)
            yield break;
        var qualified = model.ReferencesTo(symbol)
            .Where(other => other is { IsDeclaration: false, InUse: false, IsAlias: false }
                && !Edits.IsQualified(tree, other.Span))
            .Select(other => new Edit(tree, other.Span, path))
            .ToList();
        if (qualified.Count == 0)
            yield break;
        qualified.AddRange(UseItems.Without(model, name));
        yield return new Change($"Write `{name}` as `{path}`", CodeActionKinds.Rewrite, qualified);
    }

    /// <summary>
    /// The name the path under the caret leads to. The caret may be on any part of the path,
    /// and the steps on the way are what it is written through rather than what it means; a
    /// module is not a symbol at all, so its part of the path is nothing the caret is on.
    /// </summary>
    private static SymbolReference? PathAt(SemanticModel model, int caret)
    {
        foreach (var reference in model.References)
        {
            if (reference is not { IsDeclaration: false, InUse: false, IsStep: false })
                continue;
            var span = PathOf(model.Tree, reference.Span) ?? reference.Span;
            if (caret >= span.Start && caret <= span.End)
                return reference;
        }
        return null;
    }

    /// <summary>
    /// The whole path a name is the end of, <c>hw::vic::border</c> for its <c>border</c>, so
    /// that shortening it takes the steps away too.
    /// </summary>
    private static TextSpan? PathOf(SyntaxTree tree, TextSpan name)
    {
        var start = name.Start;
        while (true)
        {
            var at = start - 1;
            while (at >= 0 && tree.Text[at] is ' ' or '\t')
                at--;
            if (at < 1 || tree.Text[at] != ':' || tree.Text[at - 1] != ':')
                break;
            at -= 2;
            var end = at + 1;
            while (at >= 0 && (char.IsLetterOrDigit(tree.Text[at]) || tree.Text[at] == '_'))
                at--;
            if (at + 1 == end)
                break;
            start = at + 1;
        }
        return start == name.Start ? null : new TextSpan(start, name.End - start);
    }

    /// <summary>A declaration exported, or no longer exported, where the caret is on one.</summary>
    private static IEnumerable<Change> Exported(SemanticModel model, int line)
    {
        var tree = model.Tree;
        if (DeclaredOn(model, line) is not { } symbol || model.FileScope.Module is not { } module)
            yield break;
        if (symbol.Scope.Kind != ScopeKind.File || symbol.IsCheapLocal)
            yield break;

        if (!symbol.IsExported)
        {
            var tokens = LineContext.TokensOf(tree, line);
            if (tokens.Count > 0)
            {
                yield return new Change($"Export `{symbol.Name}` from `{module}`", CodeActionKinds.Rewrite,
                    [new Edit(tree, new TextSpan(tokens[0].Start, 0), ".export ")]);
            }
            yield break;
        }

        // What exports it may be the word in front of the declaration, or an item elsewhere in
        // the file; either way, taking that away is what stops the export.
        if (symbol.ExportSpan is not { } at)
            yield break;
        var exportLine = tree.GetLineIndex(at.Start);
        if (exportLine == line)
        {
            var tokens = LineContext.TokensOf(tree, line);
            var word = tokens.FindIndex(token => token.Text.Equals(".export", StringComparison.OrdinalIgnoreCase));
            if (word >= 0)
            {
                var end = word + 1 < tokens.Count ? tokens[word + 1].Start : tokens[word].Start + tokens[word].Text.Length;
                yield return new Change($"Stop exporting `{symbol.Name}`", CodeActionKinds.Rewrite,
                    [new Edit(tree, new TextSpan(tokens[word].Start, end - tokens[word].Start), "")]);
            }
            yield break;
        }
        yield return new Change($"Stop exporting `{symbol.Name}`", CodeActionKinds.Rewrite,
            [Edits.RemoveLines(tree, exportLine, exportLine)]);
    }

    /// <summary>
    /// What a routine leaves, declared: the state the analysis finds at every one of its
    /// returns, where they agree on one and the routine does not already say it.
    /// </summary>
    private static IEnumerable<Change> Leaves(ProgramAnalysis analysis, SemanticModel model, int line)
    {
        var tree = model.Tree;
        if (analysis.Cpu != Cpu.Wdc65816 || DeclaredOn(model, line) is not { Kind: SymbolKind.Proc } routine)
            yield break;
        if (routine.Signature is not { IsInterrupt: false, NeverReturns: false } signature)
            yield break;
        if (analysis.StatesFor(tree.Path) is not { } states || analysis.FlowFor(tree.Path) is not { } flow)
            yield break;

        ProcessorState? leaves = null;
        foreach (var step in flow.Regions.Where(region => region.Routine == routine)
            .SelectMany(region => region.Blocks)
            .SelectMany(block => block.Steps))
        {
            if (step.On is not null || step.Statement is not InstructionStatementSyntax instruction
                || instruction.Mnemonic.Text.ToLowerInvariant() is not ("rts" or "rtl"))
            {
                continue;
            }
            if (states.AnyBefore(instruction)?.Processor is not { } state)
                yield break;
            if (leaves is { } found && found != state)
                yield break;
            leaves = state;
        }
        if (leaves is not { } exit || exit == signature.Exit)
            yield break;

        var items = Edits.SpellState(exit);
        if (items.Length == 0)
            yield break;
        if (Edits.RoutineHead(tree, line) is not var (declared, beforeBrace))
            yield break;
        var written = declared is null ? $": {Edits.SpellState(signature.Entry)} -> {items}" : $" -> {items}";
        yield return new Change($"Declare what `{routine.Name}` leaves: `-> {items}`", CodeActionKinds.Rewrite,
            [new Edit(tree, new TextSpan(beforeBrace, 0), written)]);
    }

    /// <summary>
    /// A <c>rep</c> or <c>sep</c> that only changes the widths, written as the <c>.ensure</c>
    /// that says what it is for, and an <c>.ensure</c> written out as what it assembles to.
    /// </summary>
    private static IEnumerable<Change> Widths(ProgramAnalysis analysis, SemanticModel model, int line)
    {
        var tree = model.Tree;
        if (StatementOn(tree, line) is not { } statement)
            yield break;

        // Only an immediate sets the flags: `rep flags` is another instruction altogether, and
        // writing it as the `.ensure` its value happens to spell would change what it does.
        if (statement is InstructionStatementSyntax { Operand: ImmediateOperandSyntax immediate } instruction
            && instruction.Mnemonic.Text.ToLowerInvariant() is ("rep" or "sep") and var written
            && model.ValueOf(immediate.Value) is { Kind: ValueKind.Number } value)
        {
            var flags = value.Number;
            if (flags != 0 && (flags & ~0x30) == 0)
            {
                var width = written == "rep" ? 16 : 8;
                var items = string.Join(", ", new[]
                {
                    (flags & 0x20) != 0 ? $"a{width}" : null,
                    (flags & 0x10) != 0 ? $"i{width}" : null,
                }.OfType<string>());
                yield return new Change($"Write it as `.ensure {items}`", CodeActionKinds.Rewrite,
                    [new Edit(tree, statement.Span, $".ensure {items}")]);
            }
            yield break;
        }

        if (statement is EnsureDirectiveSyntax
            && analysis.LayoutFor(tree.Path)?.AnyOf(statement) is { Ensured: { } ensured })
        {
            var lines = new[] { (Mnemonic: "rep", Flags: ensured.Reset), (Mnemonic: "sep", Flags: ensured.Set) }
                .Where(pair => pair.Flags != 0)
                .Select(pair => $"{pair.Mnemonic} #${pair.Flags:x2}")
                .ToList();
            if (lines.Count == 0)
                yield break;
            var indent = Edits.IndentOf(tree, line);
            yield return new Change($"Write it out as `{string.Join("`, `", lines)}`", CodeActionKinds.Rewrite,
                [new Edit(tree, statement.Span, string.Join($"\n{indent}", lines))]);
        }
    }

    /// <summary>A number written in an operand, given a name of its own at the top of the file.</summary>
    private static IEnumerable<Change> Named(SemanticModel model, int caret, int line)
    {
        var tree = model.Tree;
        if (StatementOn(tree, line) is not { } statement || model.ReferenceAt(caret) is not null)
            yield break;
        var numbers = statement.DescendantNodes()
            .OfType<NumberExpressionSyntax>()
            .Select(node => node.Token)
            .Where(token => caret >= token.Span.Start && caret <= token.Span.End)
            .ToList();
        if (numbers is not [var number, ..])
            yield break;
        var text = number.Text;

        var name = Edits.UnusedName(model, "VALUE");
        var after = new[]
        {
            Edits.LastLine<UseDirectiveSyntax>(tree),
            Edits.LastLine<CpuDirectiveSyntax>(tree),
            Edits.LastLine<ModuleDirectiveSyntax>(tree),
        }.Max();
        yield return new Change($"Give `{text}` a name", CodeActionKinds.Extract,
            [
                new Edit(tree, number.Span, name),
                Edits.InsertAfter(tree, after, $"{name} = {text}"),
            ]);
    }

    /// <summary>
    /// A label given a name of its own, or made cheap. A cheap local is private to the routine
    /// around it, so a label with a name only that routine writes may become one.
    /// </summary>
    private static IEnumerable<Change> Labels(ProgramModel program, SemanticModel model, int caret)
    {
        var tree = model.Tree;
        if (model.ReferenceAt(caret) is not { IsDeclaration: true } reference
            || reference.Symbol is not { Kind: SymbolKind.Label } label)
        {
            yield break;
        }

        if (label.IsCheapLocal)
        {
            var name = Edits.UnusedName(model, label.Name);
            yield return new Change($"Give `@{label.Name}` a name of its own", CodeActionKinds.Rewrite,
                Edits.Rename(program, label, name));
            yield break;
        }

        // Everything that names it has to be inside the routine it is in, because that is as
        // far as a cheap local reaches.
        if (label.IsExported || label.Routine is not { } routine
            || Edits.BodyOf(tree, routine.DeclarationSpan.Line - 1) is not { } body)
        {
            yield break;
        }
        if (model.ReferencesTo(label).Any(other => other.Span.Start < body.Start || other.Span.End > body.End))
            yield break;
        yield return new Change($"Make `{label.Name}` a cheap local, `@{label.Name}`", CodeActionKinds.Rewrite,
            Edits.Rename(program, label, "@" + label.Name));
    }

    /// <summary>
    /// A declaration inside a routine, put in a segment block of its own, which is where
    /// something a routine owns but does not run through belongs.
    /// </summary>
    private static IEnumerable<Change> Segments(SemanticModel model, int line)
    {
        var tree = model.Tree;
        if (DeclaredOn(model, line) is not { Kind: SymbolKind.Data } data || data.Routine is null)
            yield break;
        if (StatementOn(tree, line) is not DataDeclarationSyntax)
            yield break;

        var indent = Edits.IndentOf(tree, line);
        var last = Edits.BlockEnd(tree, line);
        var written = string.Join("\n", Enumerable.Range(line, last - line + 1)
            .Select(at => Edits.Indent + tree.Text[tree.LineStarts[at]..LineContext.CodeEnd(tree, at)].TrimEnd()));

        // Which segment it goes in is the question, and the editor has no way to ask it, so
        // each is its own change. A program may declare many, and a menu of all of them is no
        // easier to read than the line it would write, so it is the few the file already puts
        // something in, and then the rest by name.
        var named = model.Symbols.Select(symbol => symbol.Segment).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var segment in model.Segments.Segments.Select(segment => segment.Name)
            .Where(name => name != data.Segment)
            .OrderByDescending(named.Contains)
            .ThenBy(name => name, StringComparer.Ordinal)
            .Take(4))
        {
            var block = $"{indent}.segment {segment} {{\n{written}\n{indent}}}\n";
            yield return new Change($"Put `{data.Name}` in a `.segment {segment}` block", CodeActionKinds.Rewrite,
                [Edits.RemoveLines(tree, line, last) with { Text = block }]);
        }
    }

    /// <summary>The symbol declared on <paramref name="line"/>, or null for a line that declares none.</summary>
    private static Symbol? DeclaredOn(SemanticModel model, int line) =>
        model.Symbols.FirstOrDefault(symbol => symbol.Tree == model.Tree && symbol.DeclarationSpan.Line - 1 == line);

    /// <summary>The statement parsed from <paramref name="line"/>, or null where the file has no such line.</summary>
    private static StatementSyntax? StatementOn(SyntaxTree tree, int line) =>
        line >= 0 && line < tree.LineCount ? tree.GetLine(line).Statement : null;
}
