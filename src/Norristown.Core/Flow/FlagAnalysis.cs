using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Works out which of the N, Z, C and V flags are known at each statement of a routine, and which
/// conditional branches therefore go one way only. It uses only what the CPU defines, so nothing
/// about memory is assumed. See <see cref="FlagState"/> for what is followed.
/// <para>
/// The analysis runs over a routine's blocks before anything else reads them. A branch it proves
/// is always taken is then a jump, and one it proves is never taken transfers nothing. The edges
/// such a branch does not take are not followed, so what the flags say on its other edge is kept.
/// </para>
/// <para>
/// A routine's entry, a label <c>.state</c> declares, and a label anything other than this
/// routine's own branches, jumps and <c>.next</c> annotations names are entered with nothing
/// known, since control may arrive there from anywhere. A call makes every flag unknown, because
/// no signature says what a routine does to the flags.
/// </para>
/// </summary>
internal sealed class FlagAnalysis
{
    private readonly SemanticModel model;
    private readonly CodeLayout layout;
    private readonly IReadOnlySet<StepKey> patched;
    private readonly Dictionary<StepKey, FlagState> before = [];

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
    /// no path the analysis follows reaches it. Only a <c>clc</c>, a <c>sec</c> and a <c>jmp</c>
    /// are kept, since those are what the suggestions ask about.
    /// </summary>
    public FlagState? Before(Step step) => before.GetValueOrDefault(step.Key);

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
    public Dictionary<int, ProvedBranch> Prove(IReadOnlyList<BasicBlock> blocks, IReadOnlySet<TextSpan> edges)
    {
        var proved = new Dictionary<int, ProvedBranch>();
        if (blocks.Count == 0)
            return proved;
        var reached = new FlagState?[blocks.Count];
        var pending = new SortedSet<int>();

        Arrive(0, FlagState.Unknown);
        foreach (var block in blocks)
        {
            if (IsEntry(block, edges))
                Arrive(block.Index, FlagState.Unknown);
        }

        while (pending.Count > 0)
        {
            var index = pending.Min;
            pending.Remove(index);
            var block = blocks[index];
            var (last, after) = Walk(block, reached[index]!, record: false);

            // What a call returns with is not known, and that includes a relative call, whose
            // branch writes no flags of its own.
            if (block.EndsInCall)
                after = FlagState.Unknown;
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
            var (last, _) = Walk(block, state, record: true);
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
    /// Returns what a branch does when the flags before it are <paramref name="state"/>, or null
    /// where the flag it tests is not known.
    /// </summary>
    private static ProvedBranch? Decide((StatusFlags Flag, bool TakenWhen) test, FlagState state) =>
        state.ValueOf(test.Flag) is { } value ? new ProvedBranch(test.Flag, value, value == test.TakenWhen) : null;

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
        foreach (var step in block.Steps)
        {
            last = state;
            if (record && step.Statement is InstructionStatementSyntax
                { MnemonicKind: MnemonicKind.Clc or MnemonicKind.Sec or MnemonicKind.Jmp })
            {
                before[step.Key] = before.TryGetValue(step.Key, out var earlier) ? earlier.Merge(state) : state;
            }
            state = After(step, state);
        }
        return (last, state);
    }

    /// <summary>Returns the flags after <paramref name="step"/> runs, from those before it.</summary>
    private FlagState After(Step step, FlagState state)
    {
        if (step.Statement is not InstructionStatementSyntax instruction)
        {
            // Bytes that are not an instruction nt65 knows, such as data that is run or the
            // instructions an `.ensure` emits, may do anything to the flags.
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
            case MnemonicKind.Lda or MnemonicKind.Ldx or MnemonicKind.Ldy when immediate is { } value:
                return state.Loaded(value, line?.Bits ?? 8);
            case MnemonicKind.Rep or MnemonicKind.Sep when immediate is { } mask:
                foreach (var flag in new[] { StatusFlags.Negative, StatusFlags.Zero, StatusFlags.Carry, StatusFlags.Overflow })
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
