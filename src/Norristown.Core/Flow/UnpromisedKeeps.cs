using Norristown.Processor;
using Norristown.Semantics;

namespace Norristown.Flow;

/// <summary>
/// Reports where a routine relies on a routine it calls keeping a register that the called
/// routine keeps but does not promise to keep. A declared <c>keeps</c> is a contract, so a
/// register it leaves out may change when the called routine's body does, even though the
/// analysis finds it kept today. The analysis still uses what it found, so the answer stays
/// accurate, and the warning says where the reliance starts.
/// <para>
/// A routine that declares no <c>keeps</c> promises what its body keeps, and one with no body
/// keeps only what it declares, so neither is ever relied on beyond its promise.
/// </para>
/// </summary>
internal static class UnpromisedKeeps
{
    /// <summary>
    /// Reports each call and tail call in <paramref name="region"/>'s routine that relies on more
    /// than the called routine promises. A call relies on a register where what follows it uses
    /// the value the register held before the call, or returns it as this routine's own
    /// <c>keeps</c> promises. A tail call relies on one where this routine promises to keep it.
    /// </summary>
    /// <param name="walk">The walk through the routine's file.</param>
    /// <param name="region">The routine.</param>
    /// <param name="of">Returns what each routine keeps, once that is settled.</param>
    /// <param name="reads">Returns what each routine reads, once that is settled.</param>
    /// <param name="readers">The routines that reach below their own entry on the stack.</param>
    /// <param name="report">Collects the warnings.</param>
    public static void Check(
        RegisterWalk walk, FlowRegion region, Func<Symbol, RoutineRegisters> of, Func<Symbol, RoutineReads> reads,
        IReadOnlySet<RoutineKey> readers, List<Diagnostic> report)
    {
        var blocks = region.Blocks;
        if (!region.IsEntered || blocks.Count == 0)
            return;
        var reached = walk.Solved(region, of, 0);
        var routine = region.Routine;
        var promised = routine.Signature?.Keeps ?? Registers.None;
        Registers[]? live = null;
        foreach (var block in blocks)
        {
            if (reached[block.Index] is not { } state || block.CallsUnknown
                || block.Calls is not [{ Signature: { Keeps: not Registers.None and var declared } } callee])
            {
                continue;
            }
            var unpromised = of(callee).Kept & ~declared;
            if (unpromised == Registers.None)
                continue;
            var call = block.Steps[^1];

            if (block.EndsInCall)
            {
                if (block.Index + 1 >= blocks.Count || !blocks[block.Index + 1].IsFallenInto)
                    continue;
                var start = block.Index + 1;

                // Most calls are followed by code that gives the registers new values, which a
                // cheap pass over the routine shows without following every path from the call.
                live ??= Live(blocks, promised, of, reads);
                if ((live[start] & unpromised) == Registers.None)
                    continue;

                // Followed from the statement after the call, a register holding its "entry"
                // value is one that holds what it held before the call. The 65816's high byte of
                // A is left out: 8-bit code keeps it without meaning to, and a later read of A
                // cannot be told from a read of its low byte alone.
                var after = walk.Solve(
                    region, of, start, fromOutside: false, RegisterState.Entered with { AHigh = RegisterValue.Written });
                var sites = new Dictionary<Registers, (Layout.Step Step, Symbol? Through)>();
                ReadsAnalysis.Of(walk, region, of, reads, readers, sites, start, after);
                foreach (var (register, (step, _)) in sites)
                {
                    if ((unpromised & register) != Registers.None)
                        Report("call", register, step, $"{RegisterEffects.Format(register)} is used here");
                }

                // A return that hands the register back unchanged relies on it as well, where this
                // routine promises to keep it.
                foreach (var end in blocks)
                {
                    if (after[end.Index] is not { } entered || end.End != BlockEnd.Return || end.Next is not null)
                        continue;
                    var left = walk.Through(end, entered, of, null);
                    foreach (var register in RegisterEffects.Each(unpromised & promised & ~Used(sites)))
                    {
                        if ((left.Whole(register).Entry & register) != Registers.None)
                            Report("call", register, end.Steps[^1], $"`{routine.DisplayName}` returns it here, promising `keeps`");
                    }
                }
                continue;
            }

            if (block.End != BlockEnd.TailCall)
                continue;
            Registers held = Registers.None;
            walk.Through(block, state, of, null, calling: before =>
            {
                foreach (var register in RegisterEffects.Each(Registers.All))
                {
                    if ((before.Whole(register).Entry & register) != Registers.None)
                        held |= register;
                }
            });
            foreach (var register in RegisterEffects.Each(unpromised & promised & held))
                Report("tail call", register, null, null);

            void Report(string how, Registers register, Layout.Step? used, string? where)
            {
                var name = RegisterEffects.Format(register);
                var related = used is { } step && where is not null
                    ? [new RelatedSpan(step.Statement.Tree.GetSpan(step.Statement.Span), where)]
                    : Array.Empty<RelatedSpan>();
                report.Add(new Diagnostic(
                    call.Statement.Tree.GetSpan(call.Statement.Span),
                    Catalogue.UnpromisedKeep.Message(
                        how, callee.DisplayName, name, RegisterEffects.Format(declared).ToLowerInvariant()),
                    related)
                {
                    Fix = new DiagnosticFix(FixKind.Keeps, name.ToLowerInvariant(), callee.DeclarationSpan),
                });
            }
        }
    }

    /// <summary>
    /// Returns, for each block, the registers whose values on entry to it some path may use
    /// before giving them new ones. It errs towards a register being used, so a register it
    /// leaves out is certainly not relied on. Returning uses the registers the routine promises
    /// to keep, and leaving for another routine, or for somewhere nt65 cannot follow, may use any.
    /// </summary>
    private static Registers[] Live(
        IReadOnlyList<BasicBlock> blocks, Registers promised, Func<Symbol, RoutineRegisters> of,
        Func<Symbol, RoutineReads> reads)
    {
        var live = new Registers[blocks.Count];
        bool changed;
        do
        {
            changed = false;
            for (var i = blocks.Count - 1; i >= 0; i--)
            {
                var block = blocks[i];
                var after = Registers.None;
                foreach (var to in ControlFlow.Onward(blocks, block))
                    after |= live[to];
                if (block.End is BlockEnd.Return)
                    after |= promised;
                else if (block.End is BlockEnd.TailCall or BlockEnd.Elsewhere or BlockEnd.Declared or BlockEnd.Fallthrough)
                    after = Registers.All;
                if (block.EndsInCall)
                {
                    after |= block.CallsUnknown ? Registers.All : Registers.None;
                    foreach (var callee in block.Calls)
                        after |= reads(callee).Complete ? reads(callee).Read : Registers.All;
                }
                for (var j = block.Steps.Count - 1; j >= 0; j--)
                    after = Before(block.Steps[j], after);
                if (after == live[i])
                    continue;
                live[i] = after;
                changed = true;
            }
        }
        while (changed);
        return live;
    }

    /// <summary>
    /// Returns the registers used before <paramref name="step"/>, from those used after it. A
    /// statement that is not an instruction may be run as data and do anything.
    /// </summary>
    private static Registers Before(Layout.Step step, Registers after)
    {
        if (step.Statement is not Syntax.InstructionStatementSyntax instruction)
            return step.Statement is Syntax.StateDirectiveSyntax or Syntax.MacroCallSyntax or Syntax.BlockSpliceSyntax
                || step.Label is not null ? after : Registers.All;
        var mnemonic = instruction.MnemonicKind;
        if (Instructions.IsCall(mnemonic))
            return after;
        var facts = Instructions.Facts(mnemonic);
        var read = facts.Reads | facts.Held | Indexes(step, instruction);
        var written = facts.Pushes is not null ? Registers.None : facts.Writes & ~read;

        // A shift or an increment through memory leaves the accumulator alone.
        if (mnemonic is Syntax.MnemonicKind.Asl or Syntax.MnemonicKind.Lsr or Syntax.MnemonicKind.Rol
                or Syntax.MnemonicKind.Ror or Syntax.MnemonicKind.Inc or Syntax.MnemonicKind.Dec
            && instruction.Operand is { } operand
            && !operand.GetText().Trim().Equals("a", StringComparison.OrdinalIgnoreCase))
        {
            written &= ~Registers.A;
        }
        return (after & ~written) | read;
    }

    /// <summary>
    /// Returns the index registers an instruction's operand adds to what it reads. In a macro
    /// body the operand may be an argument, so both are counted.
    /// </summary>
    private static Registers Indexes(Layout.Step step, Syntax.InstructionStatementSyntax instruction)
    {
        if (step.On is not null)
            return Registers.X | Registers.Y;
        var operand = instruction.Operand?.GetText().Replace(" ", "", StringComparison.Ordinal) ?? "";
        var used = Registers.None;
        if (operand.Contains(",x", StringComparison.OrdinalIgnoreCase))
            used |= Registers.X;
        if (operand.Contains(",y", StringComparison.OrdinalIgnoreCase))
            used |= Registers.Y;
        return used;
    }

    /// <summary>Returns the registers a use was already found for.</summary>
    private static Registers Used(Dictionary<Registers, (Layout.Step Step, Symbol? Through)> sites)
    {
        var used = Registers.None;
        foreach (var register in sites.Keys)
            used |= register;
        return used;
    }
}
