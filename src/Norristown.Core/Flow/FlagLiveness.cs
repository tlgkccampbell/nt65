using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Works out which of N, Z, C and V the code may still read after each statement of a file's
/// routines, before anything writes them again. A suggestion that drops an instruction may change
/// a flag only where nothing reads it afterwards.
/// <para>
/// The answer may overstate what is read but never understates it. A return, a jump out of the
/// routine and anything nt65 cannot follow read every flag, since the code that runs next is not
/// known, except that a return reads only the flags the routine promises where it declares any. A
/// call reads the flags the routine it calls reads, and keeps the rest for the code after
/// it. <c>php</c> reads every flag, because what it saves may come back with <c>plp</c>.
/// </para>
/// </summary>
internal sealed class FlagLiveness
{
    // The flags followed.
    private const StatusFlags Followed = StatusFlags.Negative | StatusFlags.Zero | StatusFlags.Carry | StatusFlags.Overflow;

    private readonly SemanticModel model;
    private readonly CodeLayout layout;
    private readonly ControlFlow flow;

    // The flags that may be read after each statement. A statement two routines share, as a
    // family's instances do, keeps what either may read.
    private readonly Dictionary<StepKey, StatusFlags> after = [];

    private FlagLiveness(SemanticModel model, CodeLayout layout, ControlFlow flow)
    {
        this.model = model;
        this.layout = layout;
        this.flow = flow;
    }

    /// <summary>Returns the flags that may be read after each statement of <paramref name="regions"/>.</summary>
    public static FlagLiveness Of(SemanticModel model, CodeLayout layout, ControlFlow flow, IEnumerable<FlowRegion> regions)
    {
        var liveness = new FlagLiveness(model, layout, flow);
        foreach (var region in regions)
            liveness.Solve(region.Routine, region.Blocks);
        return liveness;
    }

    /// <summary>
    /// Returns the flags each routine that <paramref name="regions"/> call reads, in the order of
    /// the calls, or null when <paramref name="flow"/> has no answers about what routines read.
    /// Those answers are the program's, so a liveness worked out over the same regions is the same
    /// wherever this returns the same flags.
    /// </summary>
    public static List<StatusFlags>? ReadByCalls(ControlFlow flow, IEnumerable<FlowRegion> regions)
    {
        if (flow.ReadsOf is not { } readsOf)
            return null;
        var read = new List<StatusFlags>();
        foreach (var block in regions.SelectMany(region => region.Blocks))
        {
            foreach (var callee in block.Calls)
                read.Add(FlagsOf(readsOf(callee).Assumed));
        }
        return read;
    }

    /// <summary>
    /// Returns the flags among N, Z, C and V that may be read after <paramref name="step"/> before
    /// anything writes them, which is all of them for a statement no routine reaches.
    /// </summary>
    public StatusFlags After(Step step) => after.GetValueOrDefault(step.Key, Followed);

    /// <summary>Works out the flags read after each statement of one routine's blocks.</summary>
    private void Solve(Symbol routine, IReadOnlyList<BasicBlock> blocks)
    {
        // A caller may read only the flags a routine promises to return with, where it declares
        // any. Relying on another is reported at the call, so nothing here keeps it for them.
        var returned = routine.Signature is { DeclaresExitFlags: true } signature
            ? (signature.ExitFlags.Known | signature.Results) & Followed
            : Followed;
        var entering = new StatusFlags[blocks.Count];
        var changed = true;
        while (changed)
        {
            changed = false;
            for (var index = blocks.Count - 1; index >= 0; index--)
            {
                var read = Through(blocks[index], Leaving(blocks[index], entering, returned), record: false);
                if (read != entering[index])
                {
                    entering[index] = read;
                    changed = true;
                }
            }
        }
        foreach (var block in blocks)
            Through(block, Leaving(block, entering, returned), record: true);
    }

    /// <summary>
    /// Returns the flags read after the last statement of <paramref name="block"/>, from those
    /// read on entry to each block, <paramref name="entering"/>. A return reads
    /// <paramref name="returned"/>, the flags the routine's callers may read.
    /// </summary>
    private StatusFlags Leaving(BasicBlock block, StatusFlags[] entering, StatusFlags returned)
    {
        var read = StatusFlags.None;
        foreach (var edge in block.Successors)
            read |= entering[edge.To];

        if (block.End == BlockEnd.Return)
        {
            // `rti` takes every flag back from the stack, so none that the routine set is read.
            return block.Steps is [.., { Statement: InstructionStatementSyntax { MnemonicKind: MnemonicKind.Rti } }]
                ? read
                : read | returned;
        }
        if (block.CallsUnknown || block.RunsInto is not null || block.BranchesOut
            || block.End is BlockEnd.TailCall or BlockEnd.Elsewhere or BlockEnd.Declared or BlockEnd.Fallthrough
            || (block.Successors.Count == 0 && block.End is BlockEnd.Through or BlockEnd.Branch or BlockEnd.Jump))
        {
            return Followed;
        }
        if (block.EndsInCall)
        {
            if (block.Calls.Count == 0 || flow.ReadsOf is not { } readsOf)
                return Followed;
            foreach (var callee in block.Calls)
                read |= FlagsOf(readsOf(callee).Assumed);
        }
        return read;
    }

    /// <summary>
    /// Returns the flags read on entry to <paramref name="block"/>, from those read after its
    /// last statement. With <paramref name="record"/>, it keeps what is read after each statement.
    /// </summary>
    private StatusFlags Through(BasicBlock block, StatusFlags read, bool record)
    {
        for (var i = block.Steps.Count - 1; i >= 0; i--)
        {
            var step = block.Steps[i];
            if (record)
                after[step.Key] = after.GetValueOrDefault(step.Key) | read;
            read = Before(step, read);
        }
        return read;
    }

    /// <summary>Returns the flags read before <paramref name="step"/>, from those read after it.</summary>
    private StatusFlags Before(Step step, StatusFlags read)
    {
        if (layout.HiddenPathAt(step) is { } hidden)
        {
            for (var i = hidden.Instructions.Count - 1; i >= 0; i--)
            {
                var decoded = hidden.Instructions[i];
                read = Before(decoded.Mnemonic, decoded.Mode, RegisterWalk.Immediate(decoded), read);
            }
            return read;
        }
        switch (step.Statement)
        {
            // A `.state` checks the flags it names against what the analysis proves.
            case StateDirectiveSyntax:
                return Followed;
            case InstructionStatementSyntax instruction:
                if (flow.Patched.Contains(step.Key))
                    return Followed;

                // A call keeps what it does not write for the code after it, and what it reads is
                // added where the block ends.
                if (ControlFlow.IsCall(instruction))
                    return read;
                var mode = layout.Of(instruction, step.On)?.Mode;
                return Before(instruction.MnemonicKind, mode, StepOperands.Immediate(model, layout, step), read);
            default:
                // Bytes that are not an instruction nt65 knows may read anything.
                return layout.Of(step.Statement, step.On) is { Length: > 0 } ? Followed : read;
        }
    }

    /// <summary>
    /// Returns the flags read before an instruction that runs as <paramref name="mnemonic"/> in
    /// <paramref name="mode"/>, from those read after it.
    /// </summary>
    private static StatusFlags Before(MnemonicKind mnemonic, AddressingMode? mode, long? immediate, StatusFlags read)
    {
        if (mnemonic is MnemonicKind.Php or MnemonicKind.Brk or MnemonicKind.Cop)
            return Followed;
        var written = FlagEffects.Written(mnemonic, mode, immediate);
        return ((read & ~written) | FlagEffects.Read(mnemonic)) & Followed;
    }

    /// <summary>Returns the flags among <paramref name="registers"/>, as flags.</summary>
    private static StatusFlags FlagsOf(Registers registers)
    {
        var flags = StatusFlags.None;
        foreach (var register in RegisterEffects.Each(registers & Registers.Flags))
            flags |= RegisterEffects.FlagOf(register);
        return flags;
    }
}
