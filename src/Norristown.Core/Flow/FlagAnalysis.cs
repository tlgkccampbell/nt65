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
/// names, are entered with nothing known, since control may arrive there from anywhere. A label
/// that a table names is an exception where only this routine's <c>.next</c> annotations name the
/// table, so that only the jumps they are under reach it. Their flags flow in as a branch's do. A call
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
internal sealed class FlagAnalysis : IKnownFlags
{
    private readonly SemanticModel model;
    private readonly CodeLayout layout;
    private readonly IReadOnlySet<StepKey> patched;
    private readonly IReadOnlyDictionary<StepKey, IReadOnlyList<MnemonicKind>> variants;
    private readonly InferredSignatures signatures;
    private readonly NextTargets targets;
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
    /// this file calls returns with, and <paramref name="signatures"/> the widths each is entered
    /// and left with.
    /// </summary>
    public FlagAnalysis(
        SemanticModel model, CodeLayout layout, IReadOnlySet<StepKey> patched,
        IReadOnlyDictionary<StepKey, IReadOnlyList<MnemonicKind>> variants, FlagExits exits,
        InferredSignatures signatures)
    {
        this.model = model;
        this.layout = layout;
        this.patched = patched;
        this.variants = variants;
        this.exits = exits;
        this.signatures = signatures;
        targets = new NextTargets(model, layout.Steps);
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
    /// no path the analysis follows reaches it.
    /// </summary>
    public FlagState? Before(Step step) => before.GetValueOrDefault(step.Key);

    /// <summary>
    /// Returns what every <see cref="Expansion"/> of <paramref name="statement"/> agrees is known
    /// about the flags and the registers just before it, or null where no path reaches it. An
    /// editor asks about a line, and a line in a macro body is emitted once per call, each copy
    /// with its own answer. An expansion's line belongs to the file that holds the body it
    /// expands.
    /// </summary>
    public FlagState? AnyBefore(StatementSyntax statement)
    {
        FlagState? merged = null;
        foreach (var (key, state) in before)
        {
            if (key.Position == statement.Position && (key.On?.Body?.Tree ?? model.Tree) == statement.Tree)
                merged = merged is null ? state : merged.Merge(state);
        }
        return merged;
    }

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
    /// Returns the constant the accumulator holds just before <paramref name="statement"/> in
    /// <paramref name="on"/>, or null where it is not known or no path reaches it, which is what
    /// layout asks about a block move.
    /// </summary>
    public long? Accumulator(SyntaxNode statement, Expansion? on) => Before(statement, on)?.Held.A;

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
    /// The blocks are left as they are.
    /// </summary>
    /// <param name="routine">The routine the blocks are of.</param>
    /// <param name="blocks">The routine's blocks.</param>
    /// <param name="edges">
    /// The spans of the tokens in this file's statements and <c>.next</c> annotations that made
    /// the blocks' edges. A label named anywhere else may be entered from outside.
    /// </param>
    /// <param name="dispatches">
    /// The routine's <c>.next</c> annotations under statements other than calls, each with the
    /// expansion of the statement above it. A label that only the table such a <c>.next</c> names
    /// holds is entered only by the jump the <c>.next</c> is under.
    /// </param>
    public Dictionary<int, ProvedBranch> Prove(
        Symbol routine, IReadOnlyList<BasicBlock> blocks, IReadOnlySet<TextSpan> edges,
        IReadOnlyList<(NextDirectiveSyntax Next, Expansion? On)> dispatches)
    {
        var proved = new Dictionary<int, ProvedBranch>();
        if (blocks.Count == 0)
            return proved;
        var dispatched = Dispatched(dispatches, edges);
        var seeds = new List<(int, FlagState)> { (0, FlagState.Entered(routine.Signature?.EntryFlags ?? FlagValues.None)) };
        foreach (var block in blocks)
        {
            if (IsEntry(block, edges, dispatched))
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
                Leave(ReturnedWith(block, last, through) ?? FlagState.Unknown);
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
                var target = BranchTarget(end);
                Leave(target is null ? FlagState.Unknown : Returned(target, last.Learn(test.Flag, test.TakenWhen), of, track: false));
            }
        }
        return left;

        void Leave(FlagState state) => left = left is null ? state : left.Merge(state);
    }

    /// <summary>
    /// Reports where the flags break a signature or a <c>.state</c> in <paramref name="region"/>.
    /// Every call is checked against the flags its routine needs on entry. Every return, including
    /// a <c>.next .return</c>, and every tail call, branch into another routine and run into one
    /// is checked against the flags this routine promises to return with.
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
            if (block.End == BlockEnd.Return)
            {
                if (ReturnedWith(block, last, After(end, last)) is { } returned)
                    CheckExit(region.Routine, promised, end, returned, null, report);
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
            if (block.BranchesOut && TestOf(block) is { } test && BranchTarget(end) is { } target)
            {
                // Where the branch is taken, control goes to the other routine for good, as a
                // tail call's does, knowing what the branch tested.
                var taken = last.Learn(test.Flag, test.TakenWhen);
                CheckEntry(end, target, taken, report);
                CheckExit(region.Routine, promised, end, Returned(target, taken, exits.Of), target, report);
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
    /// Returns the flags that <paramref name="block"/>, which ends in a return, hands back to the
    /// routine's caller, or null where it goes back somewhere else, as <c>rti</c> does.
    /// <paramref name="last"/> holds the flags before its last statement and
    /// <paramref name="through"/> those after it. A <c>.next .return</c> returns with what its
    /// statement leaves, as an <c>rts</c> returns with what it finds.
    /// </summary>
    private static FlagState? ReturnedWith(BasicBlock block, FlagState last, FlagState through)
    {
        if (block.Next is { ReturnToken: not null })
            return through;
        return block.Next is null && block.Steps[^1].Statement is InstructionStatementSyntax { MnemonicKind: MnemonicKind.Rts or MnemonicKind.Rtl }
            ? last
            : null;
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
    /// Returns the flags a suggestion may read just before <paramref name="step"/>, where the
    /// flags and registers are <paramref name="state"/>. A <c>clc</c> or a <c>sec</c> reads C, and
    /// so does a compare with zero after N and Z were set from its register. A load reads N and Z
    /// where its register holds a known constant, and a <c>jmp</c> on a CPU without <c>bra</c> may
    /// become a branch on any of N, Z, C and V.
    /// </summary>
    private static StatusFlags Suggested(Step step, FlagState state, Cpu cpu)
    {
        if (step.Statement is not InstructionStatementSyntax instruction)
            return StatusFlags.None;
        var mnemonic = instruction.MnemonicKind;
        if (Compared(mnemonic) is { } compared)
            return (state.Held.NzFrom & compared) != 0 ? StatusFlags.Carry : StatusFlags.None;
        return mnemonic switch
        {
            MnemonicKind.Clc or MnemonicKind.Sec => StatusFlags.Carry,
            MnemonicKind.Lda => state.Held.ValueOf(Registers.A) is null ? StatusFlags.None : StatusFlags.Negative | StatusFlags.Zero,
            MnemonicKind.Ldx => state.Held.ValueOf(Registers.X) is null ? StatusFlags.None : StatusFlags.Negative | StatusFlags.Zero,
            MnemonicKind.Ldy => state.Held.ValueOf(Registers.Y) is null ? StatusFlags.None : StatusFlags.Negative | StatusFlags.Zero,
            MnemonicKind.Jmp when !Instructions.Has(cpu, MnemonicKind.Bra) =>
                StatusFlags.Negative | StatusFlags.Zero | StatusFlags.Carry | StatusFlags.Overflow,
            _ => StatusFlags.None,
        };
    }

    /// <summary>
    /// Returns the state after the call a block ends with, which is what any of the routines it
    /// may call returns with. <paramref name="track"/> is as for the state after one call.
    /// </summary>
    private FlagState Called(BasicBlock block, FlagState state, Func<Symbol, RoutineFlags> of, bool track)
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
    /// Returns the state after a call to <paramref name="callee"/>, from <paramref name="state"/>
    /// before it, where <paramref name="of"/> says what each routine returns with. With
    /// <paramref name="track"/>, each flag records that it depends on the callee's answer.
    /// </summary>
    private FlagState Returned(Symbol callee, FlagState state, Func<Symbol, RoutineFlags> of, bool track = true) =>
        state.Returned(callee, of(callee), HeldThrough(callee), track);

    /// <summary>
    /// Returns the registers among A, X and Y that keep their constants across a call to
    /// <paramref name="callee"/>. A register keeps its constant only where the callee declares
    /// that it keeps it. On the 65816 the callee must also return the register at the width and
    /// in the mode it was entered with, because a register made wider or narrower holds another
    /// value than the one loaded.
    /// </summary>
    private Registers HeldThrough(Symbol callee)
    {
        var kept = callee.Signature?.Keeps ?? Registers.None;
        if (layout.Cpu != Cpu.Wdc65816 || kept == Registers.None)
            return kept;
        if (signatures.Of(callee) is not { Entry: var entry, Exit: var exit }
            || !(exit.E == ProcessorMode.Unchanged || (exit.E == entry.E && StateChecks.IsKnown(exit.E))))
        {
            return Registers.None;
        }
        if (!Unchanged(entry.A, exit.A))
            kept &= ~Registers.A;
        if (!Unchanged(entry.Index, exit.Index))
            kept &= ~(Registers.X | Registers.Y);
        return kept;

        static bool Unchanged(Width entry, Width exit) =>
            exit == Width.Unchanged || (exit == entry && StateChecks.IsKnown(exit));
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
    /// Returns the routine or the label that the conditional branch at <paramref name="end"/>
    /// names, or null where its operand names neither.
    /// </summary>
    private Symbol? BranchTarget(Step end)
    {
        var statement = (InstructionStatementSyntax)end.Statement;
        return Targets.Of(model, Transfers.TargetOf(statement, layout.Of(statement, end.On)?.Mode), end.On)?.Symbol;
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
    /// <paramref name="edges"/>, by a <c>.patch</c>, as the address an instruction reads or
    /// writes, or as an item of one of the <paramref name="dispatched"/> tables.
    /// </summary>
    private bool IsEntry(BasicBlock block, IReadOnlySet<TextSpan> edges, IReadOnlyList<TextSpan> dispatched)
    {
        if (block.Label is not { } label)
            return false;
        if (block.IsDeclared || label.Signature is not null || label.IsExported)
            return true;
        return model.ReferencesTo(label).Any(reference =>
            !reference.IsDeclaration && !edges.Contains(reference.Span) && !Touches(reference.Span)
            && !dispatched.Any(item => item.Contains(reference.Span)));
    }

    /// <summary>
    /// Returns the spans of the items of the lists and tables that <paramref name="dispatches"/>
    /// spread, where nothing but those annotations hands control to what the items name. A label
    /// named only there is reached only along the edges the flow graph gives the
    /// <c>.next</c>, so the flags the dispatching statement leaves flow into it as a branch's do.
    /// <para>
    /// A list or a table counts only where every item names a label or a routine, so the flow
    /// graph spreads all of it. It must not be exported, and every other name of it in the file
    /// must be one of <paramref name="edges"/> or one that only reads its bytes, as
    /// <c>lda table,x</c> does. Anything else, such as another routine's <c>.next</c> naming it,
    /// may hand control to its labels from elsewhere.
    /// </para>
    /// </summary>
    private List<TextSpan> Dispatched(
        IReadOnlyList<(NextDirectiveSyntax Next, Expansion? On)> dispatches, IReadOnlySet<TextSpan> edges)
    {
        var spans = new List<TextSpan>();
        foreach (var (next, on) in dispatches)
        {
            foreach (var name in next.Targets)
            {
                if (Targets.Of(model, name, on) is not { Symbol: var table } || table.IsExported
                    || model.ReferencesTo(table).Any(reference =>
                        !reference.IsDeclaration && !edges.Contains(reference.Span) && !Touches(reference.Span)))
                {
                    continue;
                }
                var items = targets.ItemsOf(table, on).ToList();
                if (items.Count == 0 || targets.Spread(table, on).Count() != items.Count)
                    continue;
                spans.AddRange(items.Where(each => each.Item.Tree == model.Tree).Select(each => each.Item.Span));
            }
        }
        return spans;
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
    /// each statement, where a statement that two routines share keeps what both agree on. The
    /// suggestions and the checks ask about a few kinds of statement, and an editor asks about
    /// any line. A suggestion may rely on what a called routine returns with, so the flags it
    /// reads at such a statement are recorded as consumed.
    /// </summary>
    private (FlagState Last, FlagState After) Walk(BasicBlock block, FlagState state, bool record)
    {
        var last = state;
        foreach (var step in block.Steps)
        {
            last = state;
            if (record)
            {
                Keep(before, step.Key, state);
                foreach (var flag in FlagValues.Named)
                {
                    if ((Suggested(step, state, layout.Cpu) & flag) != 0)
                        Consume(state, flag);
                }
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

            // A software interrupt runs a handler that may not even be in this program, and the
            // `rti` that comes back pulls whatever flags the handler left on the stack. An
            // operating system may return a status that way, so no flag is known after it.
            case MnemonicKind.Brk or MnemonicKind.Cop:
                return state.Forget(FlagState.Followed, shared: false);
        }

        // An add or a subtract in decimal mode sets N and Z on the NMOS 6502 from different
        // stages of the sum, and `bit` sets them from different values, so neither is one result.
        // A `rep` or a `sep` whose mask is not known may clear one of N and Z and set the other.
        var written = FlagEffects.Written(mnemonic, mode, immediate);
        var group = SyntaxFacts.BitOf(mnemonic)?.Group ?? mnemonic;
        var shared = group is not (MnemonicKind.Adc or MnemonicKind.Sbc or MnemonicKind.Isc or MnemonicKind.Rra
            or MnemonicKind.Arr or MnemonicKind.Bit or MnemonicKind.Plp or MnemonicKind.Rti
            or MnemonicKind.Rep or MnemonicKind.Sep);
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

        // A software interrupt's handler may change any register, as the register walk assumes.
        if (mnemonic is MnemonicKind.Brk or MnemonicKind.Cop)
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
    /// stage of the sum in decimal mode, so it counts only where D is known to be 0. A
    /// <c>tdc</c> or a <c>tsc</c> sets them from all 16 bits whatever A's width, which a compare of
    /// an 8-bit A does not test, and the width is not known here, so neither counts.
    /// </summary>
    private static Registers SetFrom(Cpu cpu, MnemonicKind mnemonic, AddressingMode? mode, FlagState state) => mnemonic switch
    {
        MnemonicKind.Lda or MnemonicKind.Pla or MnemonicKind.And or MnemonicKind.Ora or MnemonicKind.Eor
            or MnemonicKind.Txa or MnemonicKind.Tya => Registers.A,
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
