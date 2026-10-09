using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

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
    /// <param name="declining">
    /// Returns the routine whose <c>keeps</c> leaves out a register that a call to a routine
    /// relies on, which is that routine itself where it declares <c>keeps</c>.
    /// </param>
    /// <param name="report">Collects the warnings.</param>
    public static void Check(
        RegisterWalk walk, FlowRegion region, Func<Symbol, RoutineRegisters> of, Func<Symbol, RoutineReads> reads,
        IReadOnlySet<RoutineKey> readers, Func<Symbol, Registers, Symbol> declining, List<Diagnostic> report)
    {
        var blocks = region.Blocks;
        if (!region.IsEntered || blocks.Count == 0
            || !blocks.Any(block => block.Calls is [var called] && of(called).Unbacked != Registers.None))
        {
            return;
        }
        var reached = walk.Solved(region, of, 0);
        var routine = region.Routine;
        var promised = routine.Signature?.Keeps ?? Registers.None;
        Registers[]? live = null;
        Registers? restored = null;
        foreach (var block in blocks)
        {
            if (reached[block.Index] is not { } state || block.CallsUnknown || block.Calls is not [var callee])
                continue;
            var unpromised = of(callee).Unbacked;
            if (unpromised == Registers.None)
                continue;
            var call = block.Steps[^1];

            if (block.EndsInCall)
            {
                // Walked from the call, a `.state keeps` would seem to bring back the value from
                // before the call, when it says the routine's own entry value is back. A register
                // one restores is not followed, which can only miss a reliance.
                restored ??= Restored(blocks);
                unpromised &= ~restored.Value;
                if (unpromised == Registers.None)
                    continue;
                if (block.Index + 1 >= blocks.Count || !blocks[block.Index + 1].IsFallenInto)
                    continue;
                var start = block.Index + 1;

                // Most calls are followed by code that gives the registers new values, which a
                // cheap pass over the routine shows without following every path from the call.
                live ??= Live(walk, blocks, promised, of, reads);
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
                    {
                        Report("call", register, step, $"{RegisterEffects.Format(register)} is used here",
                            SaveAround(walk, register, call, callee, blocks[start], readers));
                    }
                }

                // A return that hands the register back unchanged relies on it as well, where this
                // routine promises to keep it. A `.next .return` is such a return as well.
                foreach (var end in blocks)
                {
                    if (after[end.Index] is not { } entered || end.End != BlockEnd.Return)
                        continue;
                    var left = walk.Through(end, entered, of, null);
                    foreach (var register in RegisterEffects.Each(unpromised & promised & ~Used(sites)))
                    {
                        if ((left.Whole(register).Entry & register) != Registers.None)
                        {
                            Report("call", register, end.Steps[^1], $"`{routine.DisplayName}` returns it here, promising `keeps`",
                                SaveAround(walk, register, call, callee, blocks[start], readers));
                        }
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
                Report("tail call", register, null, null, null);

            void Report(string how, Registers register, Layout.Step? used, string? where, DiagnosticFix? also)
            {
                var name = RegisterEffects.Format(register);
                var related = used is { } step && where is not null
                    ? [new RelatedSpan(step.Statement.Tree.GetSpan(step.Statement.Span), where)]
                    : Array.Empty<RelatedSpan>();

                // The promise was declined by the routine called, or, where that routine declares
                // no `keeps`, by one it calls in turn.
                var decliner = declining(callee, register);
                var listed = $"`keeps {RegisterEffects.Format(decliner.Signature?.Keeps ?? Registers.None).ToLowerInvariant()}`";
                var why = decliner == callee
                    ? $"which its {listed} does not promise"
                    : $"which depends on `{decliner.DisplayName}`, whose {listed} does not promise it";
                report.Add(new Diagnostic(
                    call.Statement.Tree.GetSpan(call.Statement.Span),
                    Catalogue.UnpromisedKeep.Message(how, callee.DisplayName, name, why),
                    related)
                {
                    Fix = new DiagnosticFix(FixKind.Keeps, name.ToLowerInvariant(), decliner.DeclarationSpan),
                    Also = also,
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
        RegisterWalk walk, IReadOnlyList<BasicBlock> blocks, Registers promised, Func<Symbol, RoutineRegisters> of,
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
                    after = Before(walk, block.Steps[j], after);
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
    private static Registers Before(RegisterWalk walk, Layout.Step step, Registers after)
    {
        if (step.Statement is not InstructionStatementSyntax instruction)
        {
            return step.Statement is StateDirectiveSyntax or MacroCallSyntax or BlockSpliceSyntax
                || step.Label is not null ? after : Registers.All;
        }
        var mnemonic = instruction.MnemonicKind;
        if (Instructions.IsCall(mnemonic))
            return after;
        var facts = Instructions.Facts(mnemonic);
        var (reads, writes) = walk.EffectsOf(step, instruction);
        var read = reads | facts.Held | (facts.Copies?.From ?? Registers.None);
        var written = facts.Pushes is not null ? Registers.None : writes & ~read;
        return (after & ~written) | read;
    }

    /// <summary>Returns the registers a <c>.state keeps</c> among the blocks names.</summary>
    private static Registers Restored(IReadOnlyList<BasicBlock> blocks)
    {
        var restored = Registers.None;
        foreach (var step in blocks.SelectMany(block => block.Steps))
        {
            if (step.Statement is not StateDirectiveSyntax)
                continue;
            foreach (var item in StateItem.Read(step.Statement))
            {
                if (item.Part == StatePart.Keeps)
                    restored |= item.Registers;
            }
        }
        return restored;
    }

    /// <summary>
    /// Returns the fix that saves <paramref name="register"/> on the stack before the call and
    /// restores it after, where that is safe, or null where it is not. It is safe for A, and for
    /// X and Y on a CPU with <c>phx</c> and <c>phy</c>; the carry cannot be saved alone, since
    /// <c>plp</c> restores every flag. The call must be a plain <c>jsr</c> or <c>jsl</c> on a line
    /// with no label, which a branch could reach past the save, to a routine that does not read
    /// below its own entry on the stack. The register must have the same width on both sides of
    /// the call. The pull sets N and Z, so the code after the call must set both before it reads
    /// either.
    /// </summary>
    private static DiagnosticFix? SaveAround(
        RegisterWalk walk, Registers register, Layout.Step call, Symbol callee, BasicBlock following,
        IReadOnlySet<RoutineKey> readers)
    {
        MnemonicKind? push = register switch
        {
            Registers.A => MnemonicKind.Pha,
            Registers.X => MnemonicKind.Phx,
            Registers.Y => MnemonicKind.Phy,
            _ => null,
        };
        if (push is not { } saving || !Instructions.Has(walk.Cpu, saving)
            || call.On is not null || call.Statement.Parent is LabeledLineSyntax
            || call.Statement is not InstructionStatementSyntax { MnemonicKind: MnemonicKind.Jsr or MnemonicKind.Jsl }
            || callee.Signature is { Arguments: > 0 } or { Inline: not null } || readers.Contains(RoutineKey.Of(callee))
            || following.Steps.Count == 0)
        {
            return null;
        }
        var index = register != Registers.A;
        if (walk.Wide(call, index) is not { } before || walk.Wide(following.Steps[0], index) != before)
            return null;
        return SetsNAndZFirst(following) ? new DiagnosticFix(FixKind.SaveAround, SyntaxFacts.TextOf(saving)) : null;
    }

    /// <summary>
    /// Returns whether the code at the start of <paramref name="block"/> sets both N and Z before
    /// anything reads either. A call, a <c>php</c>, and the end of the block count as reading them.
    /// </summary>
    private static bool SetsNAndZFirst(BasicBlock block)
    {
        const StatusFlags NZ = StatusFlags.Negative | StatusFlags.Zero;
        var set = StatusFlags.None;
        foreach (var step in block.Steps)
        {
            if (step.Statement is not InstructionStatementSyntax instruction)
            {
                if (step.Statement is StateDirectiveSyntax)
                    continue;
                return false;
            }
            var mnemonic = instruction.MnemonicKind;
            if (Instructions.IsCall(mnemonic) || mnemonic == MnemonicKind.Php
                || (FlagEffects.Read(mnemonic) & NZ & ~set) != StatusFlags.None)
            {
                return false;
            }

            // An immediate `bit` sets Z alone, and nothing here says which form this one is.
            set |= mnemonic == MnemonicKind.Bit ? StatusFlags.Zero : FlagEffects.Written(mnemonic, null, null) & NZ;
            if (set == NZ)
                return true;
        }
        return false;
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
