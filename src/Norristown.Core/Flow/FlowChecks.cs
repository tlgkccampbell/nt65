using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Reports what is wrong with the paths through a file, in the words its messages use.
/// <see cref="ControlFlow"/> builds each routine's blocks and the edges between them. This class
/// reports annotations that name somewhere code cannot be, labels nothing reaches, flow that runs
/// into data, a <c>.next</c> that is not needed, a <c>.fallthrough</c> whose claim is false, and
/// returns and calls that a routine's signature rules out.
/// </summary>
internal sealed class FlowChecks
{
    private readonly SemanticModel model;
    private readonly CodeLayout layout;
    private readonly ControlFlow flow;
    private readonly List<Diagnostic> diagnostics = [];
    private readonly List<RunningOn> runningOn = [];

    // Whether the file places another module or may be placed itself. In such a file a
    // `.fallthrough` may name a routine that the file's own layout cannot show comes next, so
    // the claim is left for the translation unit to check.
    private readonly bool placing;

    public FlowChecks(SemanticModel model, CodeLayout layout, ControlFlow flow)
    {
        this.model = model;
        this.layout = layout;
        this.flow = flow;
        placing = layout.PlacePoints.Count > 0
            || Placements.Declaration(model.Tree) is { } module && Placements.MarkerOf(module) != ModulePlacement.Alone;
    }

    /// <summary>Gets what the checks have found, in the order they were reported.</summary>
    public IReadOnlyList<Diagnostic> Found => diagnostics;

    /// <summary>
    /// Gets every <c>.fallthrough</c> whose claim only the translation unit can check. See
    /// <see cref="ControlFlow.RunningOn"/>.
    /// </summary>
    public IReadOnlyList<RunningOn> RunningOn => runningOn;

    /// <summary>
    /// Finds the data after each call to a routine that returns past it, and reports the data
    /// where it is not what the routine's <c>inline</c> item says. That item asks for <c>n</c>
    /// bytes of data, or one <c>.strz</c>. This rule holds on every CPU, because the call returns
    /// past the data whatever the processor. The processor never runs the data it returns past,
    /// so the blocks are built knowing which data that is.
    /// </summary>
    /// <param name="units">The statements of one routine.</param>
    /// <param name="calls">The relative calls among those statements.</param>
    /// <returns>The statements that hold the data the calls return past.</returns>
    public HashSet<ControlFlow.Unit> FindInlineData(
        IReadOnlyList<ControlFlow.Unit> units, IReadOnlyDictionary<(int Position, Expansion? On), RelativeCall> calls)
    {
        var found = new HashSet<ControlFlow.Unit>();
        for (var i = 0; i < units.Count; i++)
        {
            var step = units[i].Step;
            if (flow.CalledAt(step, ControlFlow.RelativeCallIn(calls, step)) is not { Signature.Inline: { } inline } routine)
                continue;
            var call = step.Statement;
            var name = routine.DisplayName;

            if (inline.IsStrz)
            {
                if (i + 1 < units.Count && units[i + 1].Step.Label is null
                    && units[i + 1].Step.Stream == step.Stream
                    && units[i + 1].Step.Statement is DataDirectiveSyntax text
                    && text.Directive.DirectiveKind == DirectiveKind.Strz)
                {
                    found.Add(units[i + 1]);
                }
                else
                {
                    Report(call, Catalogue.InlineDataMissing.Message(name, "one `.strz`", "none follows this one"));
                }
                continue;
            }

            if (inline.Expression is not { } count
                || model.ValueOf(count, step.On).AsNumber() is not { } bytes || bytes < 0)
            {
                Report(call, Catalogue.InlineCountNotConstant.Message(name, inline.Text));
                continue;
            }

            // The data is the run of it directly after the call, as long as it takes to make up
            // the count.
            var taken = 0L;
            for (var j = i + 1; j < units.Count && taken < bytes; j++)
            {
                if (units[j].Step.Label is not null || units[j].Step.Stream != step.Stream
                    || units[j].Step.Statement is not (DataDirectiveSyntax or DataValuesSyntax))
                    break;
                taken += layout.Of(units[j].Step.Statement, units[j].Step.On)?.Length ?? 0;
                found.Add(units[j]);
            }
            if (taken != bytes)
            {
                Report(call, Catalogue.InlineDataMissing.Message(
                    name,
                    $"{Bytes(bytes)} of data",
                    taken == 0 ? "none follows this one" : $"{Bytes(taken)} {(taken == 1 ? "follows" : "follow")} this one"));
            }
        }
        return found;

        void Report(SyntaxNode node, DiagnosticMessage message) =>
            diagnostics.Add(new Diagnostic(node.Tree.GetSpan(node.Span), Severity.Error, message));

        static string Bytes(long count) => count == 1 ? "1 byte" : $"{count} bytes";
    }

    /// <summary>
    /// Reports what is wrong with the paths through one routine, once its blocks are built.
    /// </summary>
    /// <param name="region">The routine's region.</param>
    /// <param name="units">The routine's statements.</param>
    /// <param name="inlineData">The data the routine's calls return past, from <see cref="FindInlineData"/>.</param>
    public void Check(FlowRegion region, IReadOnlyList<ControlFlow.Unit> units, HashSet<ControlFlow.Unit> inlineData)
    {
        CheckTargets(units);
        CheckUnreachableLabels(region);
        CheckDataReachedByFallingThrough(units, inlineData);
        CheckNextIsNeeded(units);
        CheckFallthrough(units);
        CheckReturnsAndCalls(region.Routine, units);
    }

    /// <summary>
    /// Reports each annotation target that is not somewhere code can be. What an annotation names
    /// has to be a label, a routine, or a list or a table of them. Which kind a name is becomes
    /// known only once every symbol has a value, so this is checked here rather than where the
    /// name was resolved.
    /// </summary>
    private void CheckTargets(IReadOnlyList<ControlFlow.Unit> units)
    {
        foreach (var annotation in units.SelectMany(unit => unit.Annotations.Select(a => (unit.Step.On, a))))
        {
            foreach (var targetName in Annotations.TargetsOf(annotation.a))
            {
                if (Targets.Of(model, targetName, annotation.On) is not { } target)
                    continue;
                if (flow.IsDataWithoutCodeLabels(target.Symbol, annotation.On))
                {
                    diagnostics.Add(new Diagnostic(targetName.Tree.GetSpan(targetName.Span),
                        ControlFlow.IsAddressData(target.Symbol)
                            ? Catalogue.NextTableHasNoLabels.Message(target.Symbol.DisplayName)
                            : Catalogue.NextTargetNotATable.Message(target.Symbol.DisplayName)));
                    continue;
                }
                if (target.Symbol.IsAddress || target.Symbol.Kind == SymbolKind.List)
                    continue;
                diagnostics.Add(new Diagnostic(targetName.Tree.GetSpan(targetName.Span),
                    Catalogue.NextTargetNotCode.Message(
    target.Symbol.DisplayName, target.Symbol.KindPhrase, Annotations.Format(annotation.a))));
            }
        }
    }

    /// <summary>
    /// Reports each label that nothing falls into and nothing names, and each unlabelled start of a
    /// nested segment block. nt65 sees every reference in the source, so such a label can only be
    /// reached in a way nt65 cannot see. This diagnostic pushes the programmer to declare how. A <c>.state</c> directly
    /// after the label declares it an entry point, which acknowledges that it is reached from
    /// somewhere nt65 cannot see. A label on data is read rather than run, so control never
    /// reaching it is expected.
    /// </summary>
    private void CheckUnreachableLabels(FlowRegion region)
    {
        if (!region.IsEntered)
            return;
        foreach (var block in region.Blocks)
        {
            // Code that opens a nested segment block with no label is somewhere fall-through
            // never goes, and nothing can name it either.
            if (block.Index > 0 && block.Label is null && block.Predecessors.Count == 0
                && block.Stream != region.Blocks[block.Index - 1].Stream
                && block.Steps is [{ Statement: InstructionStatementSyntax } first, ..])
            {
                diagnostics.Add(new Diagnostic(first.Statement.Tree.GetSpan(first.Statement.Span),
                    Catalogue.CodeUnreachable));
                continue;
            }
            if (block.Index == 0 || block.Label is not { Kind: not SymbolKind.Data } label || block.Predecessors.Count > 0
                || block.IsDeclared || block.Steps is [{ Statement: DataDirectiveSyntax or DataValuesSyntax }, ..])
            {
                continue;
            }
            if (model.ReferencesTo(label).Any(reference => !reference.IsDeclaration))
                continue;
            diagnostics.Add(new Diagnostic(label.DeclarationSpan,
                Catalogue.LabelUnreachable.Message(label.DisplayName)));
        }
    }

    /// <summary>
    /// Reports data that the instruction above falls through into, as happens with the
    /// <c>.byte $2c</c> skip trick and with opcodes ca65 lacks that are given as bytes. A
    /// <c>.next</c> on the data says where flow goes instead of through it.
    /// </summary>
    private void CheckDataReachedByFallingThrough(IReadOnlyList<ControlFlow.Unit> units, HashSet<ControlFlow.Unit> inline)
    {
        // On the 65816 flow that runs into data reaches the analysis, which cannot follow it,
        // so there the annotation is required rather than suggested.
        var severity = layout.Cpu == Cpu.Wdc65816 ? Severity.Error : Severity.Warning;
        var fromCode = false;
        int? stream = null;
        ControlFlow.Unit? before = null;
        foreach (var unit in units)
        {
            if (unit.Step.Stream != stream)
            {
                fromCode = false;
                stream = unit.Step.Stream;
            }
            if (unit.Step.Label is not null || unit.Step.IsMarker)
                continue;
            var data = unit.Step.Statement is DataDirectiveSyntax or DataValuesSyntax;

            // The data a routine returns past is skipped, and flow carries on after it.
            if (inline.Contains(unit))
            {
                fromCode = true;
                continue;
            }
            if (data && fromCode && unit.Next is null)
            {
                diagnostics.Add(new Diagnostic(
                    unit.Step.Statement.Tree.GetSpan(unit.Step.Statement.Span), severity,
                    Catalogue.RunsIntoData)
                {
                    Fix = AlwaysTaken(before),
                });
            }

            // Consecutive data lines are reported once: only the first, which code runs into.
            fromCode = !data && flow.RunsOn(unit);
            before = unit;
        }
    }

    /// <summary>
    /// Returns the fix that adds a <c>.next</c> saying a conditional branch running into data is
    /// always taken. That is what data after a branch nearly always means. The flags are known
    /// there, and the bytes after the branch are text or a table it jumps over. The fix is offered
    /// where the branch is in this file outside any expansion. It names the target as the source
    /// spells it, so the edit reads as the programmer would type it.
    /// </summary>
    private DiagnosticFix? AlwaysTaken(ControlFlow.Unit? branch)
    {
        if (branch is not { Step: { On: null, Statement: InstructionStatementSyntax statement } } || statement.Tree != model.Tree
            || BranchTarget(branch) is null
            || Transfers.TargetOf(statement, layout.Of(statement, null)?.Mode) is not { } targetExpression)
        {
            return null;
        }
        return new DiagnosticFix(FixKind.AlwaysTaken, targetExpression.GetText().Trim(), statement.Tree.GetSpan(statement.Span));
    }

    /// <summary>
    /// Reports each <c>rts</c> or <c>rtl</c> in a routine that never returns or in an interrupt
    /// handler, and each call to an interrupt handler. Neither kind of routine returns with
    /// <c>rts</c> or <c>rtl</c>, and an interrupt handler, which leaves by <c>rti</c>, is never
    /// called. These rules hold on every CPU.
    /// </summary>
    private void CheckReturnsAndCalls(Symbol routine, IReadOnlyList<ControlFlow.Unit> units)
    {
        foreach (var unit in units)
        {
            var statement = unit.Step.Statement;
            if (unit.Next is null && routine.Signature is { HasNoCaller: true } own
                && statement is InstructionStatementSyntax { MnemonicKind: MnemonicKind.Rts or MnemonicKind.Rtl } instruction)
            {
                var returned = SyntaxFacts.TextOf(instruction.MnemonicKind);
                Report(statement, own.IsInterrupt
                    ? Catalogue.HandlerReturnsNotRti.Message(routine.DisplayName, returned)
                    : Catalogue.NoreturnReturns.Message(routine.DisplayName, returned),

                    // A handler is left by `rti`, which is the instruction to use instead. A
                    // routine that never returns has no instruction that would do. It should
                    // leave some other way there, or not say `noreturn`.
                    own.IsInterrupt ? new DiagnosticFix(FixKind.Return, "rti") : null);
            }
            if (flow.CalledAt(unit.Step) is { Signature.IsInterrupt: true } handler)
            {
                Report(statement, Catalogue.HandlerCalled.Message(handler.DisplayName));
            }
        }

        void Report(SyntaxNode node, DiagnosticMessage message, DiagnosticFix? fix = null) =>
            diagnostics.Add(new Diagnostic(node.Tree.GetSpan(node.Span), Severity.Error, message) { Fix = fix });
    }

    /// <summary>
    /// Reports each <c>.next</c> that is not needed, or that names something other than a
    /// conditional branch's own target. A <c>.next</c> names where flow goes after a statement nt65
    /// cannot follow. Such a statement is an indirect jump or call, a return, a jump to a computed
    /// address, data that flow runs into, or the last statement of a nested segment block. That
    /// last statement runs into whatever the segment holds next. Under a conditional branch a
    /// <c>.next</c> names the branch's own target and nothing else, which says the branch is always
    /// taken. After any other statement nt65 already knows where flow goes, and the <c>.next</c>
    /// could only contradict it. <c>.next ?</c> ends a path wherever it stands.
    /// </summary>
    private void CheckNextIsNeeded(IReadOnlyList<ControlFlow.Unit> units)
    {
        if (units.Count == 0)
            return;
        var own = units[0].Step.Stream;
        var endsASegmentBlock = units
            .Where(unit => unit.Step.Stream != own && !unit.Step.IsMarker && unit.Step.Label is null)
            .GroupBy(unit => unit.Step.Stream)
            .Select(stream => stream.Last())
            .ToHashSet();
        foreach (var unit in units)
        {
            if (unit.Next is not { QuestionToken: null } next || endsASegmentBlock.Contains(unit))
                continue;

            // A branch that is always taken goes only to its own target.
            if (BranchTarget(unit) is { } target)
            {
                if (flow.Named(next, unit.Step.On).Select(named => named.Symbol).ToList() is [var only] && only == target
                    && next.Targets.Count == 1)
                {
                    continue;
                }
                diagnostics.Add(new Diagnostic(next.Tree.GetSpan(next.Keyword.Span), Severity.Error,
                    Catalogue.NextNotTheBranchTarget.Message($"`{unit.Step.Statement.GetText().Trim()}`", target.DisplayName)));
                continue;
            }
            if (Known(unit) is not { } does)
                continue;

            // Where the `.next` ends a routine's body, what was meant is nearly always that the
            // routine runs into the one after it, which is what `.fallthrough` says.
            var ends = next.Parent is LineSyntax line && Fallthrough.EndsABody(line);
            var rewrite = ends && next.Tree == model.Tree && next.Targets.Count == 1
                && flow.RoutineNamed(next.Targets[0], null) is not null;
            diagnostics.Add(new Diagnostic(next.Tree.GetSpan(next.Keyword.Span), Severity.Error,
                Catalogue.NextSuccessorsKnown.Message(
                    $"`{unit.Step.Statement.GetText().Trim()}`", does,
                    ends ? "; if this routine runs into the one after it, use `.fallthrough`" : ""))
            {
                Fix = rewrite ? new DiagnosticFix(FixKind.Spelling, ".fallthrough") : null,
            });
        }
    }

    /// <summary>
    /// Returns the label or routine a short or long conditional branch goes to when it is taken.
    /// It returns null for any other statement and for a branch whose target nt65 cannot read,
    /// such as <c>*+3</c>. A relative call made with <c>per</c> and a branch is a call, not a
    /// branch.
    /// </summary>
    private Symbol? BranchTarget(ControlFlow.Unit unit)
    {
        if (unit.Step.Statement is not InstructionStatementSyntax statement || flow.RelativeCallAt(unit.Step) is not null)
            return null;
        var mode = layout.Of(statement, unit.Step.On)?.Mode;
        return Transfers.Of(statement, mode) == Transfer.Branch
            && Targets.Of(model, Transfers.TargetOf(statement, mode), unit.Step.On) is { Symbol.IsAddress: true } target
                ? target.Symbol
                : null;
    }

    /// <summary>
    /// Returns where flow goes after a statement, phrased for a diagnostic message, where nt65 can
    /// work it out for itself. It returns null where nt65 cannot, which is where a <c>.next</c> is
    /// needed.
    /// </summary>
    private string? Known(ControlFlow.Unit unit)
    {
        if (unit.Step.Statement is not InstructionStatementSyntax statement)
            return null;
        if (flow.RelativeCallAt(unit.Step) is { } relative)
            return $"calls `{relative.Routine.DisplayName}` and comes back";
        var mode = layout.Of(statement, unit.Step.On)?.Mode;
        var transfer = Transfers.Of(statement, mode);
        if (transfer == Transfer.Through)
            return "continues with the next statement";
        if (transfer is not (Transfer.Branch or Transfer.Jump or Transfer.Call)
            || Targets.Of(model, Transfers.TargetOf(statement, mode), unit.Step.On) is not { Symbol.IsAddress: true } target)
        {
            return null;
        }
        var named = target.Symbol.DisplayName;
        return transfer switch
        {
            Transfer.Call => $"calls `{named}` and comes back",
            Transfer.Jump => $"goes to `{named}`",
            _ => $"goes to `{named}` or continues with the next statement",
        };
    }

    /// <summary>
    /// Reports each <c>.fallthrough</c> in this file whose claim is false. A <c>.fallthrough</c>
    /// says that every path reaching the end of the routine runs into the routine it names. That
    /// is true only when the named routine starts where this one ends, in the same segment. The
    /// named routine may be in another file, or the file may place another module or be placed
    /// itself. Whether the routine comes next can then only be decided for the whole translation
    /// unit, so the claim is recorded in <see cref="RunningOn"/> for that check.
    /// </summary>
    private void CheckFallthrough(IReadOnlyList<ControlFlow.Unit> units)
    {
        foreach (var unit in units)
        {
            if (unit.Step is not { Statement: FallthroughDirectiveSyntax { Target: { } targetName } directive, On: null } step)
                continue;
            if (Targets.Of(model, targetName, null) is not { } target)
                continue;
            if (flow.RoutineNamed(targetName, null) is not { } routine)
            {
                diagnostics.Add(new Diagnostic(targetName.Tree.GetSpan(targetName.Span),
                    Catalogue.FallthroughNotARoutine.Message(target.Symbol.DisplayName, target.Symbol.KindPhrase)));
                continue;
            }
            if (step.Segment is { } here && routine.Segment is { } there && here != there)
            {
                diagnostics.Add(new Diagnostic(targetName.Tree.GetSpan(targetName.Span),
                    Catalogue.FallthroughOtherSegment.Message(routine.DisplayName, here, there)));
                continue;
            }
            if (layout.PositionOf(directive) is { } end && routine.Tree == model.Tree && layout.PositionOf(routine) is { } start
                && start.Stream == end.Stream && start.Offset == end.End)
            {
                continue;
            }
            if (placing || routine.Tree != model.Tree)
            {
                runningOn.Add(new RunningOn(directive, null, routine, targetName));
                continue;
            }
            diagnostics.Add(new Diagnostic(targetName.Tree.GetSpan(targetName.Span),
                Catalogue.FallthroughNotAdjacent.Message(routine.DisplayName)));
        }
    }
}
