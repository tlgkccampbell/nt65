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
/// returns with what <see cref="FlagExits"/> says its routine returns with: the flags it keeps
/// as they were before the call, and the values it gives others. Every other flag is unknown
/// after it. Where a decision or a check depends on such an answer, the answer is recorded in
/// <see cref="Consumed"/>, so that the file is analyzed again if the program's answer differs.
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
    private readonly IReadOnlyDictionary<StepKey, IReadOnlyList<MnemonicKind>> variants;
    private readonly Dictionary<StepKey, FlagState> before = [];

    // What is known after each block that leaves the routine by running into another, keyed by
    // the block's last statement.
    private readonly Dictionary<StepKey, FlagState> after = [];

    // A statement two routines share, as a family's instances do, is proved only where both
    // prove the same thing, and is null where they differ.
    private readonly Dictionary<StepKey, ProvedBranch?> branches = [];

    // What the routines this file calls return with, and the answers a decision or a check here
    // depended on, by routine and flag.
    private readonly FlagExits exits;
    private readonly Dictionary<(RoutineKey Routine, StatusFlags Flag), ConsumedFlag> consumed = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="FlagAnalysis"/> class for the file that
    /// <paramref name="layout"/> laid out. <paramref name="patched"/> holds the instructions the
    /// program rewrites, about which nothing is assumed unless <paramref name="variants"/> lists
    /// the instructions a store turns one into. <paramref name="exits"/> says what each routine
    /// this file calls returns with.
    /// </summary>
    public FlagAnalysis(
        SemanticModel model, CodeLayout layout, IReadOnlySet<StepKey> patched,
        IReadOnlyDictionary<StepKey, IReadOnlyList<MnemonicKind>> variants, FlagExits exits)
    {
        this.model = model;
        this.layout = layout;
        this.patched = patched;
        this.variants = variants;
        this.exits = exits;
    }

    /// <summary>
    /// Gets the answers of <see cref="FlagExits"/> that a decision or a check in this file depended
    /// on. A file whose answers the program turns out to differ on is analyzed again with the
    /// program's.
    /// </summary>
    public IEnumerable<ConsumedFlag> Consumed => consumed.Values;

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
    /// no path the analysis follows reaches it. Only a <c>clc</c>, a <c>sec</c>, a <c>jmp</c>, a
    /// load, a compare, a <c>.state</c>, an <c>.ensure</c> and the last statement of each block
    /// are kept, since those are what the suggestions and the checks ask about.
    /// </summary>
    public FlagState? Before(Step step) => before.GetValueOrDefault(step.Key);

    /// <summary>
    /// Returns what is known about the flags just before <paramref name="statement"/> in
    /// <paramref name="on"/>, which is what layout asks about an <c>.ensure</c>.
    /// </summary>
    public FlagState? Before(SyntaxNode statement, Expansion? on) => before.GetValueOrDefault(new StepKey(statement.Position, on));

    /// <summary>
    /// Returns the flags known just before <paramref name="statement"/> in <paramref name="on"/>,
    /// or null where no path reaches it, which is what layout asks about an <c>.ensure</c>. A
    /// value a routine gave without promising it is left out, so that the <c>.ensure</c> does not
    /// rely on it.
    /// </summary>
    public FlagValues? Known(SyntaxNode statement, Expansion? on)
    {
        if (Before(statement, on) is not { } state)
            return null;
        var known = state.Known & ~(state.Unbacked | state.Quiet);
        return new FlagValues(known, state.Set & known);
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
        var seeds = new List<(int, FlagState)> { (0, FlagState.Entered(routine.Signature?.EntryFlags ?? FlagValues.None)) };
        foreach (var block in blocks)
        {
            if (IsEntry(block, edges))
                seeds.Add((block.Index, FlagState.Given(block.IsDeclared ? FlagValues.None : block.Label?.Signature?.EntryFlags ?? FlagValues.None)));
        }
        var reached = Solve(blocks, seeds, exits.Of, track: true);

        foreach (var block in blocks)
        {
            if (reached[block.Index] is not { } state)
                continue;
            var (last, left) = Walk(block, state, record: true);
            if (block.RunsInto is not null && block.Steps.Count > 0)
                Keep(after, block.Steps[^1].Key, left);
            if (TestOf(block) is not { } tested)
                continue;
            Consume(last, tested.Flag);
            var outcome = Decide(tested, last);
            if (outcome is { } branch)
                proved[block.Index] = branch;
            var key = block.Steps[^1].Key;
            branches[key] = branches.TryGetValue(key, out var earlier) && earlier != outcome ? null : outcome;
        }
        return proved;
    }

    /// <summary>
    /// Returns what the flags are where <paramref name="region"/>'s routine hands control back to
    /// its caller, on every path from its entry, or null where no path does. A return gives the
    /// flags before it, and a tail call, a branch or a run into another routine what that routine
    /// returns with. A path that goes somewhere nt65 cannot follow knows nothing. Each routine a
    /// call reaches returns with what <paramref name="of"/> says.
    /// </summary>
    public FlagState? Summarize(FlowRegion region, Func<Symbol, RoutineFlags> of)
    {
        var blocks = region.Blocks;
        if (blocks.Count == 0)
            return null;
        var reached = Solve(blocks, [(0, FlagState.Entered(region.Routine.Signature?.EntryFlags ?? FlagValues.None))], of, track: false);
        FlagState? left = null;
        foreach (var block in blocks)
        {
            if (reached[block.Index] is not { } state || block.Steps.Count == 0)
                continue;
            var (last, through) = Walk(block, state, record: false);
            var end = block.Steps[^1];
            if (block.RunsInto is { } runsInto)
                Leave(Returned(runsInto, through, of, track: false));
            else if (block.End == BlockEnd.Return)
            {
                Leave(block.Next is null && end.Statement is InstructionStatementSyntax { MnemonicKind: MnemonicKind.Rts or MnemonicKind.Rtl }
                    ? last
                    : FlagState.Unknown);
            }
            else if (block.End is BlockEnd.TailCall or BlockEnd.Declared or BlockEnd.Elsewhere
                && (block.CallsUnknown || block.Calls.Count > 0 || block.End == BlockEnd.Elsewhere))
            {
                if (block.CallsUnknown || block.Calls.Count == 0)
                    Leave(FlagState.Unknown);
                foreach (var callee in block.Calls)
                    Leave(Returned(callee, last, of, track: false));
            }
            else if (block.BranchesOut && TestOf(block) is { } test)
            {
                var statement = (InstructionStatementSyntax)end.Statement;
                var target = Targets.Of(model, Transfers.TargetOf(statement, layout.Of(statement, end.On)?.Mode), end.On)?.Symbol;
                Leave(target is null ? FlagState.Unknown : Returned(target, last.Learn(test.Flag, test.TakenWhen), of, track: false));
            }
        }
        return left;

        void Leave(FlagState state) => left = left is null ? state : left.Merge(state);
    }

    /// <summary>
    /// Reports where the flags break a signature or a <c>.state</c> in <paramref name="region"/>.
    /// Every call is checked against the flags its routine needs on entry, and every return, tail
    /// call and run into another routine against the flags this routine promises to return with.
    /// Each <c>.state</c> that gives a flag a value is checked where the flags prove another, and
    /// each <c>.ensure</c> that names a flag it cannot set is reported. A branch, a call or a
    /// return that relies on a flag a routine called on the way gave without promising it is
    /// reported too.
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
            if (ProvedAt(end) is { } proved)
                Relied(report, end, last, proved.Flag, "this branch");

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
                    CheckExit(region.Routine, promised, end, Returned(callee, last, exits.Of), callee, report);
                }
                continue;
            }
            if (block.RunsInto is { } runsInto && after.TryGetValue(end.Key, out var left))
            {
                CheckEntry(end, runsInto, left, report);
                CheckExit(region.Routine, promised, end, Returned(runsInto, left, exits.Of), runsInto, report);
            }
        }
    }

    /// <summary>
    /// Returns what a branch does when the flags before it are <paramref name="state"/>, or null
    /// where the flag it tests is not known.
    /// </summary>
    private static ProvedBranch? Decide((StatusFlags Flag, bool TakenWhen) test, FlagState state) =>
        state.ValueOf(test.Flag) is { } value ? new ProvedBranch(test.Flag, value, value == test.TakenWhen) : null;

    /// <summary>
    /// Returns the state after a call to <paramref name="callee"/>, from <paramref name="state"/>
    /// before it, where <paramref name="of"/> says what each routine returns with. With
    /// <paramref name="track"/>, each flag records that it depends on the callee's answer.
    /// </summary>
    private static FlagState Returned(Symbol callee, FlagState state, Func<Symbol, RoutineFlags> of, bool track = true) =>
        state.Returned(callee, of(callee), track);

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
    /// may call returns with. <paramref name="track"/> is as for the state after one call.
    /// </summary>
    private static FlagState Called(BasicBlock block, FlagState state, Func<Symbol, RoutineFlags> of, bool track)
    {
        if (block.CallsUnknown || block.Calls.Count == 0)
            return FlagState.Unknown;
        FlagState? returned = null;
        foreach (var callee in block.Calls)
        {
            var one = Returned(callee, state, of, track);
            returned = returned is null ? one : returned.Merge(one);
        }
        return returned!;
    }

    /// <summary>
    /// Works out the flags reaching each of <paramref name="blocks"/> from <paramref name="seeds"/>,
    /// the blocks control enters the routine at and what it brings there. A branch whose flag is
    /// known follows only the edge it takes, and each edge carries what the branch learned. With
    /// <paramref name="track"/>, the states record which routines' answers each flag depends on.
    /// </summary>
    private FlagState?[] Solve(
        IReadOnlyList<BasicBlock> blocks, IEnumerable<(int, FlagState)> seeds, Func<Symbol, RoutineFlags> of, bool track)
    {
        var reached = new FlagState?[blocks.Count];
        var pending = new SortedSet<int>();
        foreach (var (index, state) in seeds)
            Arrive(index, state);

        while (pending.Count > 0)
        {
            var index = pending.Min;
            pending.Remove(index);
            var block = blocks[index];
            var (last, after) = Walk(block, reached[index]!, record: false);

            // What a call returns with is what its routine returns with, and that includes a
            // relative call, whose branch writes no flags of its own.
            if (block.EndsInCall)
                after = Called(block, last, of, track);
            var test = TestOf(block);
            var decided = test is { } tested ? Decide(tested, last) : null;
            foreach (var edge in block.Successors)
            {
                switch (edge.Kind)
                {
                    // A call does not change the flags on its way in.
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
        return reached;

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
    /// Records the answers of the routines that what <paramref name="state"/> knows about
    /// <paramref name="flag"/> depends on, because a decision or a check depends on it.
    /// </summary>
    private void Consume(FlagState state, StatusFlags flag)
    {
        foreach (var source in state.SourcesOf(flag))
            consumed[(RoutineKey.Of(source), flag)] = new ConsumedFlag(source, flag, exits.Of(source).For(flag));
    }

    /// <summary>
    /// Reports where a decision or a check relies on <paramref name="flag"/> having the value it
    /// has in <paramref name="state"/>, where a routine called on the way gave that value without
    /// promising to. <paramref name="what"/> says what relies on it, such as "this branch".
    /// </summary>
    private void Relied(List<Diagnostic> report, Step step, FlagState state, StatusFlags flag, string what)
    {
        if ((state.Unbacked & flag) == 0 || state.ValueOf(flag) is not { } value || state.OriginOf(flag) is not { } origin)
            return;
        var item = FlagValues.Item(flag, value);
        var declared = origin.Signature is { DeclaresExitFlags: true } signature
            ? string.Join(", ", new[] { signature.ExitFlags.ToString() }.Where(text => text.Length > 0)
                .Concat(FlagValues.Named.Where(each => (signature.Results & each) != 0).Select(FlagValues.NameOf)))
            : "";
        var why = declared.Length > 0
            ? $"which its `-> {declared}` does not promise"
            : $"which `{origin.DisplayName}` does not promise";
        var at = step.Statement;
        report.Add(new Diagnostic(at.Tree.GetSpan(at.Span), Catalogue.UnpromisedFlag.Message(what, origin.DisplayName, item, why))
        {
            // The fix promises the value only where the routine returns the flag with it on every
            // path, and not where it merely passes on what this caller set.
            Fix = exits.Of(origin).Values.ValueOf(flag) == value ? new DiagnosticFix(FixKind.Exit, item, origin.DeclarationSpan) : null,
        });
    }

    /// <summary>Reports where a call reaches <paramref name="callee"/> without the flags it needs.</summary>
    private void CheckEntry(Step call, Symbol callee, FlagState state, List<Diagnostic> report)
    {
        if (SignatureOf(callee) is not { } signature)
            return;
        var needs = signature.EntryFlags;
        foreach (var flag in FlagValues.Named)
        {
            if (needs.ValueOf(flag) is not { } value)
                continue;
            Consume(state, flag);
            if (state.ValueOf(flag) == value)
            {
                Relied(report, call, state, flag, "this call");
                continue;
            }
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
            if (promised.ValueOf(flag) is not { } value)
                continue;
            Consume(state, flag);
            if (state.ValueOf(flag) == value)
            {
                Relied(report, end, state, flag, into is null ? "this return" : "this tail call");
                continue;
            }
            var returned = state.ValueOf(flag) is { } known ? $"{FlagState.Name(flag)} = {(known ? 1 : 0)}" : $"{FlagState.Name(flag)} not known";
            var why = into is null
                ? Said(flag, state)
                : $"`{into.DisplayName}`, which it hands control to, returns with {returned}";
            Report(report, end, end.Statement,
                Catalogue.ReturnFlagMismatch.Message(routine.DisplayName, FlagValues.Item(flag, value), why),
                into is null ? Ensuring(flag, value) : null);
        }
    }

    /// <summary>
    /// Reports where a <c>.state</c> gives a flag a value the flags prove it does not have. The fix
    /// gives each flag of the item the value the flags prove, or the one the item gave where they
    /// prove nothing.
    /// </summary>
    private void CheckState(Step step, FlagState state, List<Diagnostic> report)
    {
        foreach (var (item, value) in Items(step))
        {
            var wrong = new List<string>();
            var fixedValues = FlagValues.None;
            foreach (var flag in FlagValues.Named)
            {
                if ((item.Flags & flag) == 0)
                    continue;
                Consume(state, flag);
                var known = state.ValueOf(flag);
                if (known is { } other && other != value)
                    wrong.Add($"{FlagState.Name(flag)} is {(other ? 1 : 0)}");
                fixedValues = fixedValues.With(flag, known ?? value);
            }
            if (wrong.Count > 0)
            {
                Report(report, step, item.Node,
                    Catalogue.StateFlagMismatch.Message(item.Text, string.Join(" and ", wrong)),
                    new DiagnosticFix(FixKind.StateItem, fixedValues.ToString()));
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
            foreach (var flag in FlagValues.Named)
            {
                if ((item.Flags & flag) != 0 && Before(step) is { } here)
                    Consume(here, flag);
            }
            if (item.IsResult || Value(step, item) is not { } value
                || FlagValues.Named.Any(flag => (item.Flags & flag) != 0 && !Ensurable(flag, value)))
            {
                Report(report, step, item.Node, Catalogue.EnsureItemNotAWidth.Message(item.Text), null);
            }
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
    /// <paramref name="edges"/>, by a <c>.patch</c>, or as the address an instruction reads or
    /// writes.
    /// </summary>
    private bool IsEntry(BasicBlock block, IReadOnlySet<TextSpan> edges)
    {
        if (block.Label is not { } label)
            return false;
        if (block.IsDeclared || label.Signature is not null || label.IsExported)
            return true;
        return model.ReferencesTo(label).Any(reference =>
            !reference.IsDeclaration && !edges.Contains(reference.Span) && !Touches(reference.Span));
    }

    /// <summary>
    /// Returns a value indicating whether the name at <paramref name="span"/> names code only to
    /// touch its bytes, as a <c>.patch</c> target does, and as the address of an instruction that
    /// reads or writes memory there does. An immediate is a value that may be jumped to later, so
    /// it does not count.
    /// </summary>
    private bool Touches(TextSpan span)
    {
        foreach (var node in model.Tree.Root.FindToken(span.Start).Parent?.AncestorsAndSelf() ?? [])
        {
            switch (node)
            {
                case PatchDirectiveSyntax:
                    return true;
                case ImmediateOperandSyntax:
                    return false;
                case InstructionStatementSyntax instruction:
                    return !Instructions.IsControlTransfer(instruction.MnemonicKind);
            }
        }
        return false;
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
                || step.Statement is InstructionStatementSyntax { MnemonicKind: var mnemonic } && Asked(mnemonic)))
            {
                Keep(before, step.Key, state);
            }
            state = After(step, state);
        }
        return (last, state);

        // The suggestions ask about the flags before these instructions.
        static bool Asked(MnemonicKind mnemonic) => mnemonic is MnemonicKind.Clc or MnemonicKind.Sec or MnemonicKind.Jmp
            or MnemonicKind.Lda or MnemonicKind.Ldx or MnemonicKind.Ldy or MnemonicKind.Cmp or MnemonicKind.Cpx or MnemonicKind.Cpy;
    }

    /// <summary>Returns the flags after <paramref name="step"/> runs, from those before it.</summary>
    private FlagState After(Step step, FlagState state)
    {
        if (step.Statement is StateDirectiveSyntax or EnsureDirectiveSyntax)
        {
            // An `.ensure` emits only `rep`, `sep` and instructions that set the flags it names,
            // none of which change any other flag followed here.
            foreach (var (item, value) in Items(step))
                state = state.With(item.Flags, value);

            // On the 65816 an `.ensure` may emit a `rep` or a `sep`, which changes what a later
            // load of the same constant would load.
            return layout.Cpu == Cpu.Wdc65816 && step.Statement is EnsureDirectiveSyntax
                ? state.WithHeld(KnownRegisters.Unknown)
                : state;
        }
        if (layout.HiddenPathAt(step) is { } hidden)
        {
            // The bytes from a position inside an instruction run as the instructions they
            // decode as, one after another.
            foreach (var decoded in hidden.Instructions)
                state = After(layout.Cpu, decoded.Mnemonic, decoded.Mode, 8, RegisterWalk.Immediate(decoded), state);
            return state;
        }
        if (step.Statement is not InstructionStatementSyntax instruction)
        {
            // Bytes that are not an instruction nt65 knows, such as data that is run, may do
            // anything to the flags.
            return layout.Of(step.Statement, step.On) is { Length: > 0 } ? FlagState.Unknown : state;
        }
        if (ControlFlow.IsCall(instruction))
            return FlagState.Unknown;
        var line = layout.Of(instruction, step.On);
        var immediate = StepOperands.Immediate(model, layout, step);
        if (!patched.Contains(step.Key))
            return After(layout.Cpu, instruction.MnemonicKind, line?.Mode, line?.Bits ?? 8, immediate, state);

        // A store may turn the instruction into another, and then the flags are what either
        // leaves. Where nothing says what the store writes, nothing is known.
        if (!variants.TryGetValue(step.Key, out var listed))
            return FlagState.Unknown;
        var after = After(layout.Cpu, instruction.MnemonicKind, line?.Mode, line?.Bits ?? 8, immediate, state);
        foreach (var variant in listed)
            after = after.Merge(After(layout.Cpu, variant, line?.Mode, line?.Bits ?? 8, immediate, state));
        return after;
    }

    /// <summary>
    /// Returns the flags, and what is known about A, X and Y, after an instruction runs as
    /// <paramref name="mnemonic"/> in <paramref name="mode"/> on <paramref name="cpu"/>, from
    /// those before it. <paramref name="bits"/> is how wide its immediate is, and
    /// <paramref name="immediate"/> the immediate's value where it has one. Where the result that
    /// N and Z are set from is a known constant, N and Z are known too.
    /// </summary>
    private static FlagState After(
        Cpu cpu, MnemonicKind mnemonic, AddressingMode? mode, int bits, long? immediate, FlagState state)
    {
        var flags = FlagsAfter(mnemonic, mode, bits, immediate, state);
        var (held, carry) = Held(cpu, mnemonic, mode, bits, immediate, state);
        if (carry is { } carried)
            flags = flags.With(StatusFlags.Carry, carried);
        var nz = StatusFlags.Negative | StatusFlags.Zero;
        if ((flags.Known & nz) != nz
            && RegisterEffects.Each(held.NzFrom).Select(held.ValueOf).FirstOrDefault(value => value is not null) is { } result)
        {
            flags = flags.Loaded(result, bits);
        }
        return flags.WithHeld(held);
    }

    /// <summary>
    /// Returns the flags after an instruction runs as <paramref name="mnemonic"/> in
    /// <paramref name="mode"/>, from those before it. <paramref name="bits"/> is how wide its
    /// immediate is, and <paramref name="immediate"/> the immediate's value where it has one.
    /// </summary>
    private static FlagState FlagsAfter(MnemonicKind mnemonic, AddressingMode? mode, int bits, long? immediate, FlagState state)
    {
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
                return state.Loaded(value, bits);
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
        var written = FlagEffects.Written(mnemonic, mode, immediate);
        var group = SyntaxFacts.BitOf(mnemonic)?.Group ?? mnemonic;
        var shared = group is not (MnemonicKind.Adc or MnemonicKind.Sbc or MnemonicKind.Isc or MnemonicKind.Rra
            or MnemonicKind.Arr or MnemonicKind.Bit or MnemonicKind.Plp or MnemonicKind.Rti);
        return state.Forget(written, shared);
    }

    /// <summary>
    /// Returns what is known about A, X and Y after an instruction runs as
    /// <paramref name="mnemonic"/> on <paramref name="cpu"/>, and the carry it leaves where a
    /// shift of a known accumulator makes that known. On the 65816 only an immediate load gives a
    /// register a constant, since the arithmetic depends on widths this analysis does not follow,
    /// and a change of width forgets every constant.
    /// </summary>
    private static (KnownRegisters Held, bool? Carry) Held(
        Cpu cpu, MnemonicKind mnemonic, AddressingMode? mode, int bits, long? immediate, FlagState state)
    {
        var before = state.Held;
        var wide = cpu == Cpu.Wdc65816;
        if (wide && mnemonic is MnemonicKind.Rep or MnemonicKind.Sep or MnemonicKind.Xce or MnemonicKind.Plp)
            return (KnownRegisters.Unknown, null);

        var written = RegisterEffects.Written(mnemonic, mode, immediate);
        var held = before.Forget(written & (Registers.A | Registers.X | Registers.Y));
        if ((FlagEffects.Written(mnemonic, mode, immediate) & (StatusFlags.Negative | StatusFlags.Zero)) != 0)
            held = held with { NzFrom = Registers.None };

        var from = SetFrom(cpu, mnemonic, mode, state);
        if (from == Registers.None)
        {
            // A compare with zero sets N and Z from the register itself.
            if (immediate == 0 && Compared(mnemonic) is { } compared)
                held = held with { NzFrom = compared | ((before.NzFrom & compared) != 0 ? before.NzFrom : Registers.None) };
            return (held, null);
        }

        var mask = (1L << bits) - 1;
        var a = before.A;
        var carry = state.ValueOf(StatusFlags.Carry);
        var shift = mode == AddressingMode.Accumulator;
        long? value = mnemonic switch
        {
            MnemonicKind.Lda or MnemonicKind.Ldx or MnemonicKind.Ldy when mode == AddressingMode.Immediate => immediate & mask,
            _ when wide => null,
            MnemonicKind.Inx => (before.X + 1) & 0xff,
            MnemonicKind.Dex => (before.X - 1) & 0xff,
            MnemonicKind.Iny => (before.Y + 1) & 0xff,
            MnemonicKind.Dey => (before.Y - 1) & 0xff,
            MnemonicKind.Inc when shift => (a + 1) & 0xff,
            MnemonicKind.Dec when shift => (a - 1) & 0xff,
            MnemonicKind.And when mode == AddressingMode.Immediate => immediate == 0 ? 0 : a & immediate,
            MnemonicKind.Ora when mode == AddressingMode.Immediate => (immediate & 0xff) == 0xff ? 0xff : a | (immediate & 0xff),
            MnemonicKind.Eor when mode == AddressingMode.Immediate => a ^ (immediate & 0xff),
            MnemonicKind.Asl when shift => (a << 1) & 0xff,
            MnemonicKind.Lsr when shift => a >> 1,
            MnemonicKind.Rol when shift && carry is { } c => ((a << 1) | (c ? 1L : 0L)) & 0xff,
            MnemonicKind.Ror when shift && carry is { } c => (a >> 1) | (c ? 0x80L : 0L),
            _ => RegisterEffects.Moved(mnemonic) is (var source, _) ? before.ValueOf(source) : null,
        };

        // A shift of a known accumulator also says what it shifts into the carry.
        bool? shiftedOut = null;
        if (!wide && shift && a is { } old)
        {
            shiftedOut = mnemonic switch
            {
                MnemonicKind.Asl => (old & 0x80) != 0,
                MnemonicKind.Rol when carry is not null => (old & 0x80) != 0,
                MnemonicKind.Lsr => (old & 1) != 0,
                MnemonicKind.Ror when carry is not null => (old & 1) != 0,
                _ => null,
            };
        }
        foreach (var register in RegisterEffects.Each(from))
            held = held.With(register, value);

        // After a transfer both registers hold the result, except on the 65816, where their
        // widths may differ.
        if (!wide && RegisterEffects.Moved(mnemonic) is (var copied, _))
            from |= copied;
        return (held with { NzFrom = from }, shiftedOut);
    }

    /// <summary>
    /// Returns the registers whose new value an instruction sets N and Z from, or none where it
    /// sets them from anything else. An add or a subtract on the NMOS 6502 sets them from another
    /// stage of the sum in decimal mode, so it counts only where D is known to be 0.
    /// </summary>
    private static Registers SetFrom(Cpu cpu, MnemonicKind mnemonic, AddressingMode? mode, FlagState state) => mnemonic switch
    {
        MnemonicKind.Lda or MnemonicKind.Pla or MnemonicKind.And or MnemonicKind.Ora or MnemonicKind.Eor
            or MnemonicKind.Txa or MnemonicKind.Tya or MnemonicKind.Tdc or MnemonicKind.Tsc => Registers.A,
        MnemonicKind.Asl or MnemonicKind.Lsr or MnemonicKind.Rol or MnemonicKind.Ror or MnemonicKind.Inc or MnemonicKind.Dec
            when mode == AddressingMode.Accumulator => Registers.A,
        MnemonicKind.Adc or MnemonicKind.Sbc
            when cpu is not (Cpu.Mos6502 or Cpu.Mos6502X) || state.ValueOf(StatusFlags.Decimal) == false => Registers.A,
        MnemonicKind.Ldx or MnemonicKind.Plx or MnemonicKind.Inx or MnemonicKind.Dex or MnemonicKind.Tax
            or MnemonicKind.Tsx or MnemonicKind.Tyx => Registers.X,
        MnemonicKind.Ldy or MnemonicKind.Ply or MnemonicKind.Iny or MnemonicKind.Dey or MnemonicKind.Tay
            or MnemonicKind.Txy => Registers.Y,
        MnemonicKind.Lax => Registers.A | Registers.X,
        _ => Registers.None,
    };

    /// <summary>Returns the register a compare compares, or null for any other instruction.</summary>
    internal static Registers? Compared(MnemonicKind mnemonic) => mnemonic switch
    {
        MnemonicKind.Cmp => Registers.A,
        MnemonicKind.Cpx => Registers.X,
        MnemonicKind.Cpy => Registers.Y,
        _ => null,
    };
}
