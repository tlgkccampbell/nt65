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

    // A line of a macro body serves every call, so code there that one expansion never reaches
    // is reported only where no expansion of the line is reached. The first holds what would be
    // reported for each such line, at each call that leaves it unreached, and the second the lines
    // some expansion reaches.
    private readonly Dictionary<SyntaxNode, List<Diagnostic>> unreachedInExpansions = [];
    private readonly HashSet<SyntaxNode> reachedInExpansions = [];

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
    public IReadOnlyList<Diagnostic> Found =>
        [.. diagnostics, .. unreachedInExpansions.Where(pair => !reachedInExpansions.Contains(pair.Key)).SelectMany(pair => pair.Value)];

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
        IReadOnlyList<ControlFlow.Unit> units, IReadOnlyDictionary<StepKey, RelativeCall> calls)
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
                    Report(call, step.On, Catalogue.InlineDataMissing.Message(name, "one `.strz`", "none follows this one"));
                }
                continue;
            }

            if (inline.Expression is not { } count
                || model.ValueOf(count, step.On).AsNumber() is not { } bytes || bytes < 0)
            {
                Report(call, step.On, Catalogue.InlineCountNotConstant.Message(name, inline.Text));
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
                Report(call, step.On, Catalogue.InlineDataMissing.Message(
                    name,
                    $"{Bytes(bytes)} of data",
                    taken == 0 ? "none follows this one" : $"{Bytes(taken)} {(taken == 1 ? "follows" : "follow")} this one"));
            }
        }
        return found;

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
        CheckPatchVariants(units);
        CheckUnlistedPatches(units);
        CheckMissedPatches(units);
    }

    /// <summary>
    /// Reports each instruction a <c>.patch … as</c> lists that cannot stand in for the patched
    /// instruction, as <see cref="PatchVariant.Problem"/> decides.
    /// </summary>
    private void CheckPatchVariants(IReadOnlyList<ControlFlow.Unit> units)
    {
        if (flow.ListedVariants.Count == 0)
            return;
        var keys = units.Select(unit => unit.Step.Key).ToHashSet();
        foreach (var variant in flow.ListedVariants)
        {
            if (keys.Contains(variant.Written.Key) && variant.Problem is { } problem)
            {
                Report(variant.Name, variant.On, Catalogue.PatchVariantRejected.Message(
                    variant.Name.GetText().Trim(), SyntaxFacts.TextOf(variant.WrittenMnemonic), problem));
            }
        }
    }

    /// <summary>
    /// Reports each store that may write the opcode of a patched instruction under a
    /// <c>.patch</c> that lists no variants. Where the store can be seen to write one instruction,
    /// the fix lists it.
    /// </summary>
    private void CheckUnlistedPatches(IReadOnlyList<ControlFlow.Unit> units)
    {
        if (flow.UnlistedPatches.Count == 0)
            return;
        var keys = units.Select(unit => unit.Step.Key).ToHashSet();
        foreach (var unlisted in flow.UnlistedPatches)
        {
            if (!keys.Contains(unlisted.Written.Key))
                continue;
            var patch = unlisted.Patch;
            Report(patch, unlisted.Store.On, Catalogue.PatchVariantsRequired.Message(
                    unlisted.Store.Statement.GetTextOnOneLine(), unlisted.Label.DisplayName),
                unlisted.Inferred == MnemonicKind.None
                    ? null
                    : new DiagnosticFix(FixKind.Variant, SyntaxFacts.TextOf(unlisted.Inferred)));
        }
    }

    /// <summary>
    /// Reports each store whose bytes reach outside the instruction its <c>.patch</c> names, into
    /// something that no other <c>.patch</c> under the store names. Where they land in a labeled
    /// instruction, the fix names that label with a <c>.patch</c>.
    /// </summary>
    private void CheckMissedPatches(IReadOnlyList<ControlFlow.Unit> units)
    {
        if (flow.MissedPatches.Count == 0)
            return;
        var keys = units.Select(unit => unit.Step.Key).ToHashSet();
        foreach (var missed in flow.MissedPatches)
        {
            if (!keys.Contains(missed.Written.Key))
                continue;
            var patch = missed.Patch;
            Report(patch, missed.Store.On, Catalogue.PatchMissesStore.Message(
                    missed.Store.Statement.GetTextOnOneLine(), missed.Before ? "before" : "past", missed.Label.DisplayName,
                    missed.Length == 1 ? "1 byte" : $"{missed.Length} bytes", missed.Into),
                missed.Fix);
        }
    }

    /// <summary>
    /// Reports each annotation target that is not somewhere code can be. What an annotation names
    /// has to be a label, a routine, or a list or a table of them, in a segment the annotated code
    /// can see. Which kind a name is becomes known only once every symbol has a value, so this is
    /// checked here rather than where the name was resolved.
    /// </summary>
    private void CheckTargets(IReadOnlyList<ControlFlow.Unit> units)
    {
        foreach (var annotation in units.SelectMany(unit => unit.Annotations.Select(a => (unit.Step.On, unit.Step.Segment, a))))
        {
            foreach (var targetName in Annotations.TargetsOf(annotation.a))
            {
                if (Targets.Of(model, targetName, annotation.On) is not { } target)
                    continue;
                if (flow.IsDataWithoutCodeLabels(target.Symbol, annotation.On))
                {
                    Report(targetName, annotation.On,
                        ControlFlow.IsAddressData(target.Symbol)
                            ? Catalogue.NextTableHasNoLabels.Message(target.Symbol.DisplayName)
                            : Catalogue.NextTargetNotATable.Message(target.Symbol.DisplayName));
                    continue;
                }
                if (target.Symbol.IsAddress
                    && model.Segments.Reach(annotation.Segment, target.Symbol.Segment) == SegmentReach.NeverMapped)
                {
                    var (here, there) = model.Segments.Find(annotation.Segment!)!
                        .Excluding(model.Segments.Find(target.Symbol.Segment!)!)!.Value;
                    var what = $"`{target.Symbol.QualifiedName}`";
                    Report(targetName, annotation.On, Catalogue.SegmentNotVisible.Message(
                        annotation.a is PatchDirectiveSyntax ? what : $"`{Annotations.Format(annotation.a)}` targets {what}",
                        target.Symbol.Segment!, annotation.Segment!, there.Config, there.Area, here.Area));
                    continue;
                }
                if (target.Symbol.IsAddress || target.Symbol.Kind == SymbolKind.List)
                    continue;
                Report(targetName, annotation.On, Catalogue.NextTargetNotCode.Message(
                    target.Symbol.DisplayName, target.Symbol.KindPhrase, Annotations.Format(annotation.a)));
            }
        }
    }

    /// <summary>
    /// Reports each label that nothing falls into and nothing names, and each run of code that
    /// nothing reaches, such as the code after a return or a jump that has no label. nt65 sees every reference in the source, so such a label can only be
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
            // Code with no label that nothing falls into is reached by nothing, since nothing can
            // name it. That is code after a transfer that ends the path, or code that opens a
            // nested segment block, which fall-through never enters. The run is reported once, at
            // its first instruction.
            var unreached = block.Index > 0 && block.Label is null && block.Predecessors.Count == 0;
            if (!unreached)
                reachedInExpansions.UnionWith(block.Steps.Where(step => step.On is not null).Select(step => step.Statement));
            if (unreached && block.Steps is [{ Statement: InstructionStatementSyntax } first, ..])
            {
                var above = region.Blocks[block.Index - 1];
                var found = Expansion.Problem(model.Tree, first.Statement, first.On, null,
                    Catalogue.CodeUnreachable.Message(block.Stream != above.Stream
                        ? "execution does not fall into a nested segment block, so start it with a label that is "
                            + "jumped to, named by a `.next`, or declared by a `.state`"
                        : above.Steps is [.., var branch] && flow.Flags?.ProvedAt(branch) is { Taken: true } proved
                            ? $"`{branch.Statement.GetText().Trim()}` above is always taken, because {proved.Why}, "
                                + "and nothing branches or jumps here"
                            : "the statement above does not fall through, and nothing branches or jumps here"));

                // Only the code itself is faded. Reported at a call, the call is still needed.
                found = found with { IsUnnecessary = found.Related.Count == 0 };
                if (first.On is null)
                    diagnostics.Add(found);
                else if (unreachedInExpansions.TryGetValue(first.Statement, out var calls))
                    calls.Add(found);
                else
                    unreachedInExpansions[first.Statement] = [found];
                continue;
            }
            if (block.Index == 0 || block.Label is not { Kind: not SymbolKind.Data } label || block.Predecessors.Count > 0
                || block.IsDeclared || block.Steps is [{ Statement: DataDirectiveSyntax or DataValuesSyntax }, ..])
            {
                continue;
            }
            if (model.ReferencesTo(label).Any(reference => !reference.IsDeclaration))
                continue;
            diagnostics.Add(Expansion.Problem(model.Tree, label.Tree, label.NameSpan, block.On, null,
                Catalogue.LabelUnreachable.Message(label.DisplayName),
                new DiagnosticFix(FixKind.State, At: label.DeclarationSpan)));
        }
    }

    /// <summary>
    /// Reports data that the instruction above falls through into, as happens with the
    /// <c>.byte $2c</c> skip trick and with opcodes ca65 lacks that are given as bytes. A
    /// <c>.next</c> on the data says where flow goes instead of through it. The analysis cannot
    /// follow flow into data on any CPU, so the annotation is required on every CPU. Data in a
    /// macro body is reported at the call, with the body line as a note. Where it is the last
    /// thing the call expands to, the <c>.next</c> goes after the call, and otherwise in the body.
    /// </summary>
    private void CheckDataReachedByFallingThrough(IReadOnlyList<ControlFlow.Unit> units, HashSet<ControlFlow.Unit> inline)
    {
        var fromCode = false;
        int? stream = null;
        ControlFlow.Unit? before = null;
        for (var index = 0; index < units.Count; index++)
        {
            var unit = units[index];
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
                ReportRunIntoData(units, index, AlwaysTaken(before));

            // Consecutive data lines are reported once: only the first, which code runs into.
            fromCode = !data && flow.RunsOn(unit);
            before = unit;
        }
    }

    /// <summary>
    /// Reports the data at <paramref name="index"/> in <paramref name="units"/>, which the
    /// statement above falls into, with <paramref name="fix"/> where one applies.
    /// </summary>
    private void ReportRunIntoData(IReadOnlyList<ControlFlow.Unit> units, int index, DiagnosticFix? fix)
    {
        var unit = units[index];
        var statement = unit.Step.Statement;
        var what = PaddingFill.Of(unit.Step, model) is null ? "data" : "padding";
        var call = Expansion.BodyLine(statement, unit.Step.On, model.Tree);

        // Where the data is the last thing a call expands to, a `.next` after the call applies to
        // it. Anywhere else in a body, the `.next` has to go in the body, after the data.
        var after = what == "data" ? "after the data" : "after it";
        string who = "the instruction above", where = $"this {what}";
        if (call is var (at, holder, level))
        {
            var last = !units.Skip(index + 1).Any(next => next.Step.Stream == unit.Step.Stream
                && next.Step.On?.IsWithin(level) == true);
            who = "execution";
            where = last
                ? $"the {what} that the call to `{at.Name.Text}!` ends in"
                : $"{what} in the body of `{holder.Name.Text}!`";
            after = last ? "after the call" : $"after that {what} in the macro body";
        }

        // Padding whose fill does not run on cannot take a `.next`, so the message asks for a fill
        // that does, or a jump over it.
        var message = PaddingFill.Of(unit.Step, model) switch
        {
            { } fill when !fill.RunsOn(layout.Cpu) => Catalogue.RunsIntoData.Message(who, where,
                fill.WhyNot() + $"; give it a fill byte that runs on, such as `$ea` (`nop`), and add a `.next` {after}, "
                    + "or `jmp` over it"),
            _ => Catalogue.RunsIntoData.Message(who, where, $"add a `.next` {after} saying where flow goes"),
        };
        var found = call is var (reported, _, _)
            ? new Diagnostic(reported.Tree.GetSpan(reported.Span), message,
                [new RelatedSpan(statement.Tree.GetSpan(statement.Span), Expansion.InTheMacroBody)])
            : new Diagnostic(statement.Tree.GetSpan(statement.Span), message);
        diagnostics.Add(found with { Fix = fix });
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
    /// handler, each call to an interrupt handler or to a label inside one, and each call to a
    /// routine that declares <c>pulls n</c>. Neither of the first two kinds of routine returns with
    /// <c>rts</c> or <c>rtl</c>, and an interrupt handler, which leaves by <c>rti</c>, is never
    /// called. A call through a pointer is checked against each target its <c>.next</c> names.
    /// It also reports each <c>rti</c> in a routine not marked <c>interrupt</c>, which the
    /// processor may enter in any state. These rules hold on every CPU.
    /// </summary>
    private void CheckReturnsAndCalls(Symbol routine, IReadOnlyList<ControlFlow.Unit> units)
    {
        foreach (var unit in units)
        {
            var statement = unit.Step.Statement;
            var returned = unit.Next switch
            {
                null when statement is InstructionStatementSyntax { MnemonicKind: MnemonicKind.Rts or MnemonicKind.Rtl } instruction
                    => SyntaxFacts.TextOf(instruction.MnemonicKind),
                { ReturnToken: not null } => ".next .return",
                _ => null,
            };
            if (returned is not null && routine.Signature is { HasNoCaller: true } own)
            {
                Report(statement, unit.Step.On, own.IsInterrupt
                    ? Catalogue.HandlerReturnsNotRti.Message(routine.DisplayName, returned)
                    : Catalogue.NoreturnReturns.Message(routine.DisplayName, returned),

                    // A handler is left by `rti`, which is the instruction to use instead. A
                    // routine that never returns has no instruction that would do. It should
                    // leave some other way there, or not say `noreturn`. A `.next .return` has
                    // no instruction to replace.
                    own.IsInterrupt && unit.Next is null ? new DiagnosticFix(FixKind.Return, "rti") : null);
            }
            foreach (var called in Callees(unit))
            {
                switch (called)
                {
                    case { Signature.IsInterrupt: true } handler:
                        Report(statement, unit.Step.On,
                            Catalogue.HandlerCalled.Message($"`{handler.DisplayName}` is an interrupt handler", "it returns"));
                        break;

                    // The path from a label inside a handler leaves by the handler's `rti`, so a
                    // call to the label is as wrong as a call to the handler.
                    case { Kind: SymbolKind.Label, Signature: null, Routine: { Signature.IsInterrupt: true } owner } label:
                        Report(statement, unit.Step.On, Catalogue.HandlerCalled.Message(
                            $"`{label.DisplayName}` is inside interrupt handler `{owner.DisplayName}`", "the path from it returns"));
                        break;
                }

                // A call puts nothing above the return address, so it cannot hand a routine the
                // bytes `pulls n` says it is entered with. A label is entered as its routine is.
                if (RegisterWalk.Owner(called) is { Signature.Pulls: > 0 and var pulls } pulling)
                    Report(statement, unit.Step.On, Catalogue.PullsRoutineCalled.Message(pulling.DisplayName, pulls));
            }

            // An `rti` with a `.next` is a computed jump that says where it goes, and is not a
            // handler's return.
            if (statement is InstructionStatementSyntax { MnemonicKind: MnemonicKind.Rti } && unit.Next is null
                && routine.Signature is { IsInterrupt: false })
            {
                Report(statement, unit.Step.On, Catalogue.RtiOutsideHandler.Message(routine.DisplayName),
                    routine.Tree == model.Tree ? new DiagnosticFix(FixKind.Interrupt, At: routine.DeclarationSpan) : null);
            }
        }

        // A direct call names its routine in its operand. A call nt65 cannot follow names the
        // routines and labels it may reach in its `.next`, and each of them is checked as a direct
        // call's would be.
        IEnumerable<Symbol> Callees(ControlFlow.Unit unit) =>
            flow.CalledAt(unit.Step) is { } direct ? [direct]
                : unit.Next is { } next && ControlFlow.IsCall(unit.Step.Statement)
                    ? flow.Named(next, unit.Step.On).Select(named => named.Symbol).Distinct()
                    : [];
    }

    /// <summary>
    /// Reports each <c>.next</c> that is not needed, or that names something other than a
    /// conditional branch's own target. A <c>.next</c> names where flow goes after a statement nt65
    /// cannot follow. Such a statement is an indirect jump or call, a return, a jump to a computed
    /// address, data that flow runs into, or the last statement of a nested segment block. That
    /// last statement runs into whatever the segment holds next. Under a conditional branch a
    /// <c>.next</c> names the branch's own target and nothing else, which says the branch is always
    /// taken. After any other statement nt65 already knows where flow goes, and the <c>.next</c>
    /// could only contradict it. <c>.next ?</c> says control goes somewhere nt65 is not told about,
    /// and may stand anywhere but under a conditional branch. A branch goes only to its operand or
    /// to the next statement, so neither of its ways on is unknown, and neither is a return. So
    /// <c>.next .return</c> cannot stand there either, nor under a call, which comes back, nor
    /// under a return, which already goes back to the caller.
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
            var statement = $"`{unit.Step.Statement.GetText().Trim()}`";
            if (unit.Next is { } claimed && (claimed.QuestionToken ?? claimed.ReturnToken) is { } unknown && IsBranch(unit))
            {
                Report(claimed, unit.Step.On,
                    Catalogue.NextUnknownAfterBranch.Message(
                        $".next {unknown.Text}",
                        statement,
                        BranchTarget(unit) is { } taken
                            ? $"if the branch is always taken, write `.next {taken.DisplayName}`, and otherwise remove the `.next`"
                            : "write the label the branch goes to as its operand"));
                continue;
            }

            // A call comes back, and a return already goes back to the caller, so neither is a
            // place for `.next .return`.
            if (unit.Next is { ReturnToken: not null } returning && unit.Step.Statement is InstructionStatementSyntax instruction)
            {
                if (Instructions.IsCall(instruction.MnemonicKind) || flow.RelativeCallAt(unit.Step) is not null)
                {
                    Report(returning, unit.Step.On, Catalogue.ReturnAfterCall.Message(statement));
                    continue;
                }
                if (instruction.MnemonicKind is MnemonicKind.Rts or MnemonicKind.Rtl or MnemonicKind.Rti)
                {
                    Report(returning.Keyword, unit.Step.On,
                        Catalogue.NextSuccessorsKnown.Message(statement, "returns to its caller", ""));
                    continue;
                }
            }
            if (unit.Next is not { QuestionToken: null } next || endsASegmentBlock.Contains(unit))
                continue;

            // A branch that is always taken goes only to its own target.
            if (BranchTarget(unit) is { } target)
            {
                if (flow.Named(next, unit.Step.On).Select(named => named.Symbol).ToList() is [var only] && only == target
                    && next.Targets.Count == 1)
                {
                    // The flags may show that the branch the `.next` calls always taken never is.
                    if (flow.Flags?.ProvedAt(unit.Step) is { Taken: false } never)
                    {
                        Report(next, unit.Step.On,
                            Catalogue.NextNeverTaken.Message(target.DisplayName, unit.Step.Statement.GetText().Trim(), never.Why));
                    }
                    continue;
                }
                Report(next.Keyword, unit.Step.On,
                    Catalogue.NextNotTheBranchTarget.Message($"`{unit.Step.Statement.GetText().Trim()}`", target.DisplayName));
                continue;
            }
            if (Known(unit) is not { } does)
                continue;

            // Where the `.next` ends a routine's body, what was meant is nearly always that the
            // routine runs into the one after it, which is what `.fallthrough` says.
            var ends = next.Parent is LineSyntax line && Fallthrough.EndsABody(line);
            var rewrite = ends && next.Tree == model.Tree && next.Targets.Count == 1
                && flow.RoutineNamed(next.Targets[0], null) is not null;
            Report(next.Keyword, unit.Step.On,
                Catalogue.NextSuccessorsKnown.Message(
                    $"`{unit.Step.Statement.GetText().Trim()}`", does,
                    ends ? "; if this routine runs into the one after it, use `.fallthrough`" : ""),
                rewrite ? new DiagnosticFix(FixKind.Spelling, ".fallthrough") : null);
        }
    }

    /// <summary>
    /// Returns whether a statement is a short or long conditional branch, whether or not nt65 can
    /// read its target. A relative call made with <c>per</c> and a branch is a call, not a branch.
    /// </summary>
    private bool IsBranch(ControlFlow.Unit unit) =>
        unit.Step.Statement is InstructionStatementSyntax statement && flow.RelativeCallAt(unit.Step) is null
            && Transfers.Of(statement, layout.Of(statement, unit.Step.On)?.Mode) == Transfer.Branch;

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

    /// <summary>
    /// Reports a problem with <paramref name="node"/>, found in the expansion <paramref name="on"/>.
    /// A line of a macro body is reported at the call that expanded it, with the line as a note
    /// and without <paramref name="fix"/>, as
    /// <see cref="Expansion.Problem(SyntaxTree, SyntaxNode, Expansion?, Severity?, DiagnosticMessage, DiagnosticFix?)"/>
    /// describes.
    /// </summary>
    private void Report(SyntaxNode node, Expansion? on, DiagnosticMessage message, DiagnosticFix? fix = null) =>
        diagnostics.Add(Expansion.Problem(model.Tree, node, on, null, message, fix));

    /// <summary>
    /// Reports a problem with <paramref name="token"/>, found in the expansion <paramref name="on"/>.
    /// A token on a line of a macro body is reported at the call that expanded it, with the token
    /// as a note and without <paramref name="fix"/>.
    /// </summary>
    private void Report(SyntaxToken token, Expansion? on, DiagnosticMessage message, DiagnosticFix? fix = null) =>
        diagnostics.Add(Expansion.Problem(model.Tree, token.Parent.Tree, token.Span, on, null, message, fix));
}
