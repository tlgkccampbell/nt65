using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// The refactorings offered at a selection, independent of any diagnostic: a name switched
/// between its full path and a <c>.use</c>, the <c>.use</c> items organized, a declaration
/// exported or unexported, a routine's exit state declared, a width change rewritten as an
/// <c>.ensure</c> or an <c>.ensure</c> written out, a number given a name, a label given an
/// ordinary name or made a cheap local, a declaration moved into a segment block, a macro call
/// inlined, code extracted into a routine, and ca65 converted to nt65.
/// <para>
/// Each is offered only where it would change something, and each is worked out from the
/// analysis rather than from the text alone, so a rewrite keeps the line's meaning.
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

        // Most refactorings look at the caret or its line; extraction and ca65 conversion look
        // at every line the selection covers.
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
            .. InlineMacro.In(analysis, model, caret),
            .. ExtractProc.In(analysis, model, range),
            .. Ca65Conversion.In(model, range),
        ];
    }

    /// <summary>
    /// Switches how a name declared in another module is written: one written as a full path
    /// is shortened and brought in with a <c>.use</c>, and one a <c>.use</c> brought in is
    /// written out as its full path, with the <c>.use</c> item removed since nothing then
    /// needs it.
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
            // Shorten every use of the path in this file, so that the file writes the name one
            // way; the one `.use` covers them all.
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

        // The other direction: write the full path wherever this file uses the short name, and
        // remove the `.use` item, which then has nothing left to do. An alias is left alone,
        // because its name is this file's own choice rather than a shorthand for the path.
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
    /// The reference to the name at the end of the path under the caret. The caret may be on
    /// any part of the path: the steps before the last are only how the name is reached, not
    /// what it refers to, and a module part of the path is not a symbol at all.
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

        // The export may be an `.export` in front of the declaration or a separate `.export`
        // line elsewhere in the file; removing it stops the export.
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
    /// Declares a routine's exit state: the processor state the analysis finds at every one of
    /// its returns, offered where all the returns agree and the routine does not already
    /// declare that state.
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
    /// A <c>rep</c> or <c>sep</c> that only changes the register widths, rewritten as the
    /// <c>.ensure</c> that states its purpose, and an <c>.ensure</c> written out as the
    /// instructions it assembles to.
    /// </summary>
    private static IEnumerable<Change> Widths(ProgramAnalysis analysis, SemanticModel model, int line)
    {
        var tree = model.Tree;
        if (StatementOn(tree, line) is not { } statement)
            yield break;

        // Only an immediate operand is a known set of flags: `rep flags` without the `#` is a
        // different instruction, and rewriting it as the `.ensure` its value happens to match
        // would change what it does.
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

    /// <summary>
    /// A number in a statement replaced by a named constant, declared near the top of the file
    /// after its <c>.module</c>, <c>.cpu</c> and <c>.use</c> lines.
    /// </summary>
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
    /// A cheap-local label given an ordinary name, or an ordinary label made a cheap local. A
    /// cheap local is private to its enclosing routine, so a label can become one only if every
    /// reference to it is inside that routine.
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

        // Every reference has to be inside the label's routine, because that is as far as a
        // cheap local can be seen.
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
    /// A data declaration inside a routine, moved into a segment block of its own, which is where
    /// data a routine owns but does not execute belongs.
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

        // The editor has no way to ask which segment, so each choice is its own change. A
        // program may declare many segments, and a menu of all of them would be no easier than
        // writing the line by hand, so at most four are offered: those this file already puts
        // something in first, then the rest alphabetically.
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
