using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Works out which routines depend on how deep the stack was when they were entered. Such a
/// routine pulls what its caller pushed, reads the stack pointer, or addresses the stack by
/// offset, as a routine that pops its own return address to leave two levels at once does. A
/// routine that passes control to such a routine, by calling it, jumping or branching into it,
/// or running into it, depends on the depth too. A call to a routine that takes <c>args</c> is
/// the exception, because what that routine reads is what its caller pushed for it.
/// <para>
/// A tail call leaves the stack one return address shallower than a call does, so it is safe
/// only where the routine called depends on nothing below its own entry.
/// </para>
/// </summary>
internal static class CallerStack
{
    /// <summary>
    /// Returns the routines of <paramref name="files"/> that depend on the depth of the stack
    /// they were entered with. A routine whose body is not in the program is taken not to, as
    /// its signature is the whole of what is known about it.
    /// </summary>
    public static IReadOnlySet<RoutineKey> Readers(IReadOnlyList<FileAnalysis> files)
    {
        var readers = new HashSet<RoutineKey>();
        var reaches = new Dictionary<RoutineKey, HashSet<RoutineKey>>();
        foreach (var file in files)
        {
            foreach (var region in file.Flow.Regions)
            {
                var key = RoutineKey.Of(region.Routine);
                var targets = reaches.TryGetValue(key, out var known) ? known : reaches[key] = [];
                if (Reads(file, region, targets))
                    readers.Add(key);
            }
        }

        // A routine that passes control to one that reads its caller's stack reads it too. The
        // set only grows, so this stops.
        bool moved;
        do
        {
            moved = false;
            foreach (var (key, targets) in reaches)
            {
                if (!readers.Contains(key) && targets.Any(readers.Contains))
                    moved = readers.Add(key) || moved;
            }
        }
        while (moved);
        return readers;
    }

    /// <summary>
    /// Returns whether a routine's own code depends on the depth of the stack it was entered
    /// with, and adds to <paramref name="targets"/> each other routine it passes control to. A
    /// transfer nt65 cannot follow could reach anything, so it counts as depending on the depth.
    /// </summary>
    private static bool Reads(FileAnalysis file, FlowRegion region, HashSet<RoutineKey> targets)
    {
        var model = file.Model;
        var reads = false;
        foreach (var block in region.Blocks)
        {
            if (!block.IsReached)
                continue;
            if (block.CallsUnknown)
                reads = true;
            // A routine that takes `args` reads its arguments where its caller pushed them, which
            // is within the caller's own part of the stack. A tail call to the caller leaves them
            // where they were.
            var calls = file.Flow.EndsInCall(block);
            foreach (var called in block.Calls)
            {
                if (!calls || called.Signature is not { Arguments: > 0 })
                    Add(called);
            }
            if (block.RunsInto is { } runsInto)
                Add(runsInto);

            foreach (var step in block.Steps)
            {
                if (step.Statement is not InstructionStatementSyntax statement)
                    continue;
                var mnemonic = statement.MnemonicKind;
                var mode = file.Layout.Of(statement, step.On)?.Mode;
                if (mnemonic is MnemonicKind.Tsx or MnemonicKind.Tsc
                    || mode is AddressingMode.StackRelative or AddressingMode.StackRelativeIndirectY
                    || (Instructions.Facts(mnemonic).Pulls is not null && !Pushed(file, step)))
                {
                    reads = true;
                }
            }

            if (block.Steps is not [.., var last] || last.Statement is not InstructionStatementSyntax ending)
                continue;
            if (block.Next is { } next)
            {
                foreach (var (named, _) in file.Flow.Named(next, last.On))
                    Add(named);
                continue;
            }
            var lastMode = file.Layout.Of(ending, last.On)?.Mode;
            var transfer = Transfers.Of(ending, lastMode);
            if (transfer is Transfer.Branch or Transfer.Jump)
            {
                if (Targets.Of(model, Transfers.TargetOf(ending, lastMode), last.On)?.Symbol is { } target)
                    Add(target);
                else
                    reads = true;
            }
            else if (transfer == Transfer.Elsewhere)
            {
                reads = true;
            }
        }
        return reads;

        // On the 65816 the state analysis counts the bytes pushed, which the registers' stack of
        // saves cannot do across a push of one width pulled at another.
        static bool Pushed(FileAnalysis file, Layout.Step step) => file.State is { } states
            ? states.Before(step.Statement, step.On)?.Stack is { IsAnchored: true, Depth: > 0 }
            : file.Flow.Registers?.Before(step.Statement, step.On)?.Stack is { Depth: > 0 };

        void Add(Symbol target)
        {
            var owner = target is { Kind: SymbolKind.Label, Routine: { } routine } ? routine : target;
            if (owner.Signature is not null && owner != region.Routine)
                targets.Add(RoutineKey.Of(owner));
        }
    }
}
