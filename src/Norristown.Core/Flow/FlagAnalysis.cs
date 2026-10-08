using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Works out which of the N, Z, C, V, D and I flags are known at each statement of a routine, and
/// which conditional branches therefore go one way only. It uses only what the CPU defines, so
/// nothing about memory is assumed. See <see cref="FlagState"/> for what is followed.
/// <para>
/// The analysis runs over a routine's blocks before anything else reads them. A branch it proves
/// is always taken is then a jump, and one it proves is never taken transfers nothing. The edges
/// such a branch does not take are not followed, so what the flags say on its other edge is kept.
/// </para>
/// <para>
/// A routine's entry, and a label with a signature of its own, are entered with the flags the
/// signature gives before <c>-&gt;</c>, and nothing else known. A label <c>.state</c> declares, and
/// a label anything other than this routine's own branches, jumps and <c>.next</c> annotations
/// names, are entered with nothing known, since control may arrive there from anywhere. A call
/// returns with the flags its routine's signature gives after <c>-&gt;</c>, and with the flags its
/// <c>keeps</c> names as they were before the call. Every other flag is unknown after it.
/// </para>
/// <para>
/// A <c>.state</c> gives the flags it names their values, and is checked where the flags prove
/// another value. An <c>.ensure</c> gives them their values whatever it emits to do so.
/// </para>
/// </summary>
internal sealed class FlagAnalysis
{
    private readonly SemanticModel model;
    private readonly CodeLayout layout;
    private readonly IReadOnlySet<StepKey> patched;
    private readonly Dictionary<StepKey, FlagState> before = [];

    // What is known after each block that leaves the routine by running into another, keyed by
    // the block's last statement.
    private readonly Dictionary<StepKey, FlagState> after = [];

    // A statement two routines share, as a family's instances do, is proved only where both
    // prove the same thing, and is null where they differ.
    private readonly Dictionary<StepKey, ProvedBranch?> branches = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="FlagAnalysis"/> class for the file that
    /// <paramref name="layout"/> laid out. <paramref name="patched"/> holds the instructions the
    /// program rewrites, about which nothing is assumed.
    /// </summary>
    public FlagAnalysis(SemanticModel model, CodeLayout layout, IReadOnlySet<StepKey> patched)
    {
        this.model = model;
        this.layout = layout;
        this.patched = patched;
    }

    /// <summary>
    /// Returns the flag a conditional branch tests and the value that makes it branch, or null for
    /// any other instruction. A branch that tests a bit of memory, such as <c>bbr0</c>, is not
    /// counted.
    /// </summary>
    public static (StatusFlags Flag, bool TakenWhen)? Tested(MnemonicKind mnemonic) => mnemonic switch
    {
        MnemonicKind.Bcc or MnemonicKind.Jcc => (StatusFlags.Carry, false),
        MnemonicKind.Bcs or MnemonicKind.Jcs => (StatusFlags.Carry, true),
        MnemonicKind.Bne or MnemonicKind.Jne => (StatusFlags.Zero, false),
        MnemonicKind.Beq or MnemonicKind.Jeq => (StatusFlags.Zero, true),
        MnemonicKind.Bpl or MnemonicKind.Jpl => (StatusFlags.Negative, false),
        MnemonicKind.Bmi or MnemonicKind.Jmi => (StatusFlags.Negative, true),
        MnemonicKind.Bvc or MnemonicKind.Jvc => (StatusFlags.Overflow, false),
        MnemonicKind.Bvs or MnemonicKind.Jvs => (StatusFlags.Overflow, true),
        _ => null,
    };

    /// <summary>
    /// Returns the short branch that is taken when <paramref name="flag"/> is
    /// <paramref name="value"/>.
    /// </summary>
    public static MnemonicKind BranchWhen(StatusFlags flag, bool value) => flag switch
    {
        StatusFlags.Carry => value ? MnemonicKind.Bcs : MnemonicKind.Bcc,
        StatusFlags.Zero => value ? MnemonicKind.Beq : MnemonicKind.Bne,
        StatusFlags.Negative => value ? MnemonicKind.Bmi : MnemonicKind.Bpl,
        _ => value ? MnemonicKind.Bvs : MnemonicKind.Bvc,
    };

    /// <summary>
    /// Returns what is known about the flags just before <paramref name="step"/>, or null where
    /// no path the analysis follows reaches it. Only a <c>clc</c>, a <c>sec</c>, a <c>.state</c>,
    /// an <c>.ensure</c> and the last statement of each block are kept, since those are what the
    /// suggestions and the checks ask about.
    /// </summary>
    public FlagState? Before(Step step) => before.GetValueOrDefault(step.Key);

    /// <summary>
    /// Returns what is known about the flags just before <paramref name="statement"/> in
    /// <paramref name="on"/>, which is what layout asks about an <c>.ensure</c>.
    /// </summary>
    public FlagState? Before(SyntaxNode statement, Expansion? on) => before.GetValueOrDefault(new StepKey(statement.Position, on));

    /// <summary>
    /// Returns the flags known just before <paramref name="statement"/> in <paramref name="on"/>,
    /// or null where no path reaches it, which is what layout asks about an <c>.ensure</c>.
    /// </summary>
    public FlagValues? Known(SyntaxNode statement, Expansion? on) =>
        Before(statement, on) is { } state ? new FlagValues(state.Known, state.Set) : null;

    /// <summary>
    /// Returns the state after a call to <paramref name="callee"/>, from <paramref name="state"/>
    /// before it. The routine's signature gives the flags it returns with and the flags it keeps.
    /// A label with no signature of its own returns as the routine it is in does.
    /// </summary>
    public static FlagState Returned(Symbol callee, FlagState state)
    {
        if (SignatureOf(callee) is not { IsInterrupt: false } signature)
            return FlagState.Unknown;
        return state.Returned(Kept(signature.Keeps), signature.ExitFlags);
    }

    /// <summary>
    /// Returns the signature a call to <paramref name="target"/> is checked against: its own, or,
    /// for a label with none, that of the routine it is in.
    /// </summary>
    public static Signature? SignatureOf(Symbol target) =>
        target.Signature ?? (target.Kind == SymbolKind.Label ? target.Routine?.Signature : null);

    /// <summary>
    /// Returns what the conditional branch at <paramref name="step"/> does, where the analysis
    /// proves it goes one way only on every path, or null elsewhere.
    /// </summary>
    public ProvedBranch? ProvedAt(Step step) => branches.GetValueOrDefault(step.Key);

    /// <summary>
    /// Works out the flags through one routine's <paramref name="blocks"/>, and returns each
    /// block whose last statement is a conditional branch the analysis proves goes one way only.
    /// The blocks are left as they are. <paramref name="edges"/> holds the spans of the tokens
    /// in this file's statements and <c>.next</c> annotations that made the blocks' edges, so a
    /// label named anywhere else may be entered from outside.
    /// </summary>
    public Dictionary<int, ProvedBranch> Prove(Symbol routine, IReadOnlyList<BasicBlock> blocks, IReadOnlySet<TextSpan> edges)
    {
        var proved = new Dictionary<int, ProvedBranch>();
        if (blocks.Count == 0)
            return proved;
        var reached = new FlagState?[blocks.Count];
        var pending = new SortedSet<int>();

        Arrive(0, FlagState.Given(routine.Signature?.EntryFlags ?? FlagValues.None));
        foreach (var block in blocks)
        {
            if (IsEntry(block, edges))
                Arrive(block.Index, FlagState.Given(block.IsDeclared ? FlagValues.None : block.Label?.Signature?.EntryFlags ?? FlagValues.None));
        }

        while (pending.Count > 0)
        {
            var index = pending.Min;
            pending.Remove(index);
            var block = blocks[index];
            var (last, after) = Walk(block, reached[index]!, record: false);

            // What a call returns with is what its routine's signature says, and that includes a
            // relative call, whose branch writes no flags of its own.
            if (block.EndsInCall)
                after = Called(block, last);
            var test = TestOf(block);
            var decided = test is { } tested ? Decide(tested, last) : null;
            foreach (var edge in block.Successors)
            {
                switch (edge.Kind)
                {
                    // A call does not change the flags on its way in, but what it returns with
                    // is not known.
                    case EdgeKind.Call:
                        Arrive(edge.To, last);
                        break;
                    case EdgeKind.FallThrough when block.End == BlockEnd.Branch && test is { } branch:
                        if (decided is not { Taken: true })
                            Arrive(edge.To, after.Learn(branch.Flag, !branch.TakenWhen));
                        break;
                    case EdgeKind.Taken when block.End == BlockEnd.Branch && test is { } branch:
                        if (decided is not { Taken: false })
                            Arrive(edge.To, after.Learn(branch.Flag, branch.TakenWhen));
                        break;
                    default:
                        Arrive(edge.To, after);
                        break;
                }
            }
        }

        foreach (var block in blocks)
        {
            if (reached[block.Index] is not { } state)
                continue;
            var (last, left) = Walk(block, state, record: true);
            if (block.RunsInto is not null && block.Steps.Count > 0)
                Keep(after, block.Steps[^1].Key, left);
            if (TestOf(block) is not { } tested)
                continue;
            var outcome = Decide(tested, last);
            if (outcome is { } branch)
                proved[block.Index] = branch;
            var key = block.Steps[^1].Key;
            branches[key] = branches.TryGetValue(key, out var earlier) && earlier != outcome ? null : outcome;
        }
        return proved;

        void Arrive(int to, FlagState state)
        {
            var merged = reached[to] is { } here ? here.Merge(state) : state;
            if (merged.Equals(reached[to]))
                return;
            reached[to] = merged;
            pending.Add(to);
        }
    }

    /// <summary>
    /// Reports where the flags break a signature or a <c>.state</c> in <paramref name="region"/>.
    /// Every call is checked against the flags its routine needs on entry, and every return, tail
    /// call and run into another routine against the flags this routine promises to return with.
    /// Each <c>.state</c> that gives a flag a value is checked where the flags prove another, and
    /// each <c>.ensure</c> that names a flag it cannot set is reported.
    /// </summary>
    public void Check(FlowRegion region, List<Diagnostic> report)
    {
        var promised = region.Routine.Signature is { IsInterrupt: false, NeverReturns: false } own ? own.ExitFlags : FlagValues.None;
        foreach (var block in region.Blocks)
        {
            foreach (var step in block.Steps)
            {
                if (step.Statement is StateDirectiveSyntax && Before(step) is { } here)
                    CheckState(step, here, report);
                else if (step.Statement is EnsureDirectiveSyntax)
                    CheckEnsure(step, report);
            }
            if (block.Steps.Count == 0 || Before(block.Steps[^1]) is not { } last)
                continue;
            var end = block.Steps[^1];

            if (block.EndsInCall && !block.CallsUnknown)
            {
                foreach (var callee in block.Calls)
                    CheckEntry(end, callee, last, report);
                continue;
            }
            if (block.End == BlockEnd.Return && block.Next is null
                && end.Statement is InstructionStatementSyntax { MnemonicKind: MnemonicKind.Rts or MnemonicKind.Rtl })
            {
                CheckExit(region.Routine, promised, end, last, null, report);
                continue;
            }
            if (block.End == BlockEnd.TailCall && !block.CallsUnknown)
            {
                foreach (var callee in block.Calls)
                {
                    CheckEntry(end, callee, last, report);
                    CheckExit(region.Routine, promised, end, Returned(callee, last), callee, report);
                }
                continue;
            }
            if (block.RunsInto is { } runsInto && after.TryGetValue(end.Key, out var left))
            {
                CheckEntry(end, runsInto, left, report);
                CheckExit(region.Routine, promised, end, Returned(runsInto, left), runsInto, report);
            }
        }
    }

    /// <summary>
    /// Returns what a branch does when the flags before it are <paramref name="state"/>, or null
    /// where the flag it tests is not known.
    /// </summary>
    private static ProvedBranch? Decide((StatusFlags Flag, bool TakenWhen) test, FlagState state) =>
        state.ValueOf(test.Flag) is { } value ? new ProvedBranch(test.Flag, value, value == test.TakenWhen) : null;

    /// <summary>Returns the flags among C, Z, N and V that <paramref name="registers"/> keeps.</summary>
    private static StatusFlags Kept(Registers registers)
    {
        var kept = StatusFlags.None;
        foreach (var flag in new[] { StatusFlags.Carry, StatusFlags.Zero, StatusFlags.Negative, StatusFlags.Overflow })
        {
            if ((RegisterEffects.Of(flag) & registers) != Registers.None)
                kept |= flag;
        }
        return kept;
    }

    /// <summary>Records <paramref name="state"/> for <paramref name="key"/>, keeping only what every recording agrees on.</summary>
    private static void Keep(Dictionary<StepKey, FlagState> states, StepKey key, FlagState state) =>
        states[key] = states.TryGetValue(key, out var earlier) ? earlier.Merge(state) : state;

    /// <summary>
    /// Returns the words that say what is known about <paramref name="flag"/> in
    /// <paramref name="state"/>, such as "C is 1 here" or "C is not known here".
    /// </summary>
    private static string Said(StatusFlags flag, FlagState state) =>
        state.ValueOf(flag) is { } value ? FlagState.Describe(flag, value) : $"{FlagState.Name(flag)} is not known here";

    /// <summary>
    /// Returns the fix that inserts an <c>.ensure</c> of the item that gives <paramref name="flag"/>
    /// <paramref name="value"/>, or null for an item <c>.ensure</c> cannot make true.
    /// </summary>
    private static DiagnosticFix? Ensuring(StatusFlags flag, bool value) =>
        Ensurable(flag, value) ? new DiagnosticFix(FixKind.Ensure, FlagValues.Item(flag, value)) : null;

    /// <summary>
    /// Returns whether an <c>.ensure</c> can give <paramref name="flag"/> <paramref name="value"/>
    /// with one instruction that changes nothing else. C, D and I can be cleared and set, and V
    /// only cleared.
    /// </summary>
    private static bool Ensurable(StatusFlags flag, bool value) =>
        flag is StatusFlags.Carry or StatusFlags.Decimal or StatusFlags.InterruptDisable
        || (flag == StatusFlags.Overflow && !value);

    /// <summary>
    /// Returns the state after the call a block ends with, which is what any of the routines it
    /// may call returns with.
    /// </summary>
    private static FlagState Called(BasicBlock block, FlagState state)
    {
        if (block.CallsUnknown || block.Calls.Count == 0)
            return FlagState.Unknown;
        FlagState? returned = null;
        foreach (var callee in block.Calls)
        {
            var one = Returned(callee, state);
            returned = returned is null ? one : returned.Merge(one);
        }
        return returned!;
    }

    /// <summary>Reports where a call reaches <paramref name="callee"/> without the flags it needs.</summary>
    private void CheckEntry(Step call, Symbol callee, FlagState state, List<Diagnostic> report)
    {
        if (SignatureOf(callee) is not { } signature)
            return;
        var needs = signature.EntryFlags;
        foreach (var flag in FlagValues.Named)
        {
            if (needs.ValueOf(flag) is not { } value || state.ValueOf(flag) == value)
                continue;
            Report(report, call, call.Statement,
                Catalogue.CallFlagMismatch.Message(callee.DisplayName, FlagValues.Item(flag, value), Said(flag, state)),
                Ensuring(flag, value));
        }
    }

    /// <summary>
    /// Reports where a path leaves <paramref name="routine"/> without the flags it promises to
    /// return with. <paramref name="into"/> is the routine the path hands control to, or null at
    /// a return.
    /// </summary>
    private void CheckExit(
        Symbol routine, FlagValues promised, Step end, FlagState state, Symbol? into, List<Diagnostic> report)
    {
        foreach (var flag in FlagValues.Named)
        {
            if (promised.ValueOf(flag) is not { } value || state.ValueOf(flag) == value)
                continue;
            var returned = state.ValueOf(flag) is { } known ? $"{FlagState.Name(flag)} = {(known ? 1 : 0)}" : $"{FlagState.Name(flag)} not known";
            var why = into is null
                ? Said(flag, state)
                : $"`{into.DisplayName}`, which it hands control to, returns with {returned}";
            Report(report, end, end.Statement,
                Catalogue.ReturnFlagMismatch.Message(routine.DisplayName, FlagValues.Item(flag, value), why),
                into is null ? Ensuring(flag, value) : null);
        }
    }

    /// <summary>Reports where a <c>.state</c> gives a flag a value the flags prove it does not have.</summary>
    private void CheckState(Step step, FlagState state, List<Diagnostic> report)
    {
        foreach (var (item, value) in Items(step))
        {
            if (state.ValueOf(item.Flag) is { } known && known != value)
            {
                Report(report, step, item.Node,
                    Catalogue.StateFlagMismatch.Message(item.Text, FlagState.Name(item.Flag), known ? 1 : 0),
                    new DiagnosticFix(FixKind.StateItem, FlagValues.Item(item.Flag, known)));
            }
        }
    }

    /// <summary>Reports each flag an <c>.ensure</c> names that it cannot set.</summary>
    private void CheckEnsure(Step step, List<Diagnostic> report)
    {
        foreach (var item in StateItem.Read(step.Statement))
        {
            if (item.Part != StatePart.Flag)
                continue;
            if (item.IsResult || Value(step, item) is not { } value || !Ensurable(item.Flag, value))
                Report(report, step, item.Node, Catalogue.EnsureItemNotAWidth.Message(item.Text), null);
        }
    }

    /// <summary>
    /// Returns the flag items of a <c>.state</c> or an <c>.ensure</c> that give a flag 0 or 1, with
    /// that value.
    /// </summary>
    private IEnumerable<(StateItem Item, bool Value)> Items(Step step)
    {
        foreach (var item in StateItem.Read(step.Statement))
        {
            if (item.Part == StatePart.Flag && Value(step, item) is { } value)
                yield return (item, value);
        }
    }

    /// <summary>Returns the value a flag item gives, or null where it gives none that is 0 or 1.</summary>
    private bool? Value(Step step, StateItem item) =>
        item.Expression is { } expression && model.ValueOf(expression, step.On).AsNumber() is (0 or 1) and var value
            ? value == 1
            : null;

    /// <summary>
    /// Adds an error at <paramref name="node"/>, or, where the statement comes from a macro's
    /// expansion, at the call in this file that expanded it. A fix is offered only where the
    /// statement is this file's own.
    /// </summary>
    private void Report(List<Diagnostic> report, Step step, SyntaxNode node, DiagnosticMessage message, DiagnosticFix? fix)
    {
        var at = node;
        for (var level = step.On; level is not null; level = level.Outer)
        {
            if (level.Call is { } call && call.Tree == model.Tree)
                at = call;
        }
        report.Add(new Diagnostic(at.Tree.GetSpan(at.Span), Severity.Error, message)
        {
            Fix = ReferenceEquals(at, node) && node.Tree == model.Tree ? fix : null,
        });
    }

    /// <summary>
    /// Returns the flag that the conditional branch ending <paramref name="block"/> tests, or null
    /// where the block ends some other way. A branch with a <c>.next</c> under it counts, so a
    /// check can compare the two, though its edges are the ones the <c>.next</c> names.
    /// </summary>
    private (StatusFlags Flag, bool TakenWhen)? TestOf(BasicBlock block) =>
        block.End is BlockEnd.Branch or BlockEnd.Declared
        && block.Steps is [.., { Statement: InstructionStatementSyntax branch } last]
        && !patched.Contains(last.Key)
            ? Tested(branch.MnemonicKind)
            : null;

    /// <summary>
    /// Returns whether control may enter <paramref name="block"/> from somewhere the routine's
    /// own edges do not show. That is a label a <c>.state</c> declares, a label with a
    /// signature of its own, an exported label, and a label named anywhere but at one of
    /// <paramref name="edges"/>.
    /// </summary>
    private bool IsEntry(BasicBlock block, IReadOnlySet<TextSpan> edges)
    {
        if (block.Label is not { } label)
            return false;
        if (block.IsDeclared || label.Signature is not null || label.IsExported)
            return true;
        return model.ReferencesTo(label).Any(reference => !reference.IsDeclaration && !edges.Contains(reference.Span));
    }

    /// <summary>
    /// Returns the flags before a block's last statement and after the whole block, starting from
    /// <paramref name="state"/>. With <paramref name="record"/>, it also keeps the state before
    /// each statement a suggestion asks about, where a statement that two routines share keeps
    /// what both agree on.
    /// </summary>
    private (FlagState Last, FlagState After) Walk(BasicBlock block, FlagState state, bool record)
    {
        var last = state;
        for (var i = 0; i < block.Steps.Count; i++)
        {
            var step = block.Steps[i];
            last = state;
            if (record && (i == block.Steps.Count - 1
                || step.Statement is StateDirectiveSyntax or EnsureDirectiveSyntax
                    or InstructionStatementSyntax { MnemonicKind: MnemonicKind.Clc or MnemonicKind.Sec or MnemonicKind.Jmp }))
            {
                Keep(before, step.Key, state);
            }
            state = After(step, state);
        }
        return (last, state);
    }

    /// <summary>Returns the flags after <paramref name="step"/> runs, from those before it.</summary>
    private FlagState After(Step step, FlagState state)
    {
        if (step.Statement is StateDirectiveSyntax or EnsureDirectiveSyntax)
        {
            // An `.ensure` emits only `rep`, `sep` and instructions that set the flags it names,
            // none of which change any other flag followed here.
            foreach (var (item, value) in Items(step))
                state = state.With(item.Flag, value);
            return state;
        }
        if (step.Statement is not InstructionStatementSyntax instruction)
        {
            // Bytes that are not an instruction nt65 knows, such as data that is run, may do
            // anything to the flags.
            return layout.Of(step.Statement, step.On) is { Length: > 0 } ? FlagState.Unknown : state;
        }
        var mnemonic = instruction.MnemonicKind;
        if (patched.Contains(step.Key) || ControlFlow.IsCall(instruction))
            return FlagState.Unknown;

        var line = layout.Of(instruction, step.On);
        var immediate = StepOperands.Immediate(model, layout, step);
        switch (mnemonic)
        {
            case MnemonicKind.Clc:
                return state.With(StatusFlags.Carry, false);
            case MnemonicKind.Sec:
                return state.With(StatusFlags.Carry, true);
            case MnemonicKind.Clv:
                return state.With(StatusFlags.Overflow, false);
            case MnemonicKind.Cld or MnemonicKind.Sed:
                return state.With(StatusFlags.Decimal, mnemonic == MnemonicKind.Sed);
            case MnemonicKind.Cli or MnemonicKind.Sei:
                return state.With(StatusFlags.InterruptDisable, mnemonic == MnemonicKind.Sei);
            case MnemonicKind.Lda or MnemonicKind.Ldx or MnemonicKind.Ldy when immediate is { } value:
                return state.Loaded(value, line?.Bits ?? 8);
            case MnemonicKind.Rep or MnemonicKind.Sep when immediate is { } mask:
                foreach (var flag in FlagValues.Named)
                {
                    if ((mask & (long)flag) != 0)
                        state = state.With(flag, mnemonic == MnemonicKind.Sep);
                }
                return state;
        }

        // An add or a subtract in decimal mode sets N and Z on the NMOS 6502 from different
        // stages of the sum, and `bit` sets them from different values, so neither is one result.
        var written = FlagEffects.Written(mnemonic, line?.Mode, immediate);
        var group = SyntaxFacts.BitOf(mnemonic)?.Group ?? mnemonic;
        var shared = group is not (MnemonicKind.Adc or MnemonicKind.Sbc or MnemonicKind.Isc or MnemonicKind.Rra
            or MnemonicKind.Arr or MnemonicKind.Bit or MnemonicKind.Plp or MnemonicKind.Rti);
        return state.Forget(written, shared);
    }
}
