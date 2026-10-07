using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Holds the <see cref="StackEffect"/> of every routine of the program, and of every label another
/// routine calls or jumps to, worked out from their bodies. Every stack tracker applies a
/// callee's effect after a call, so a caller whose callee leaves bytes behind, or takes some,
/// does not pair a later pull with the wrong push.
/// <para>
/// An effect is read off each exit of a routine. A return leaves what the stack holds above the
/// caller's stack there, less the return address it pulls. A tail call, a branch or a
/// <c>.fallthrough</c> into another routine leaves what that routine leaves, from the height it
/// was handed control at. An exit nt65 cannot follow leaves something unknown, and a call to a
/// routine that never returns leaves nothing, because no path goes on from it.
/// </para>
/// <para>
/// The height is counted in bytes by a walk of its own, rather than read off the stacks that
/// pair each pull with a push, because a pull of another size than the push still moves the
/// stack by a known amount. On the 65816 a push of a register whose width is as the routine was
/// entered with counts as one of that width, so a push and a pull of it cancel.
/// </para>
/// </summary>
public sealed class StackEffects
{
    // How many times one entry's effect may change before it is taken to be unknown. An effect
    // that depends on itself, as one that pushes a byte and then calls itself does, would
    // otherwise grow without end.
    private const int Changes = 8;

    private readonly Dictionary<RoutineKey, StackEffect> found;

    private StackEffects(Dictionary<RoutineKey, StackEffect> found) => this.found = found;

    /// <summary>
    /// Gets effects in which every routine leaves the stack as it found it, for an analysis that
    /// runs before the program's effects are worked out.
    /// </summary>
    public static StackEffects None { get; } = new([]);

    /// <summary>
    /// Returns the effect of a call to <paramref name="target"/>. A routine whose body is not in
    /// the program is taken to leave the stack as it found it, as its signature is the whole of
    /// what is known about it. A label inside a routine that is not an entry point of its own has
    /// no effect worked out, so it is unknown.
    /// </summary>
    public StackEffect Of(Symbol target) =>
        found.TryGetValue(RoutineKey.Of(target), out var effect) ? effect
            : target is { Kind: SymbolKind.Label, Routine: { } owner } && found.ContainsKey(RoutineKey.Of(owner))
                ? StackEffect.Unknown
                : StackEffect.Balanced;

    /// <summary>
    /// Returns the effect of the call a block ends with, which is what any of the routines it may
    /// call leaves. A call nt65 cannot follow leaves something unknown, and one that never
    /// returns leaves the stack as it is, because nothing goes on from it.
    /// </summary>
    public StackEffect OfCallIn(BasicBlock block)
    {
        if (block.CallsUnknown || block.Calls.Count == 0)
            return StackEffect.Unknown;
        var effect = StackEffect.NeverReturns;
        foreach (var callee in block.Calls)
            effect = StackEffect.Join(effect, Of(callee));
        return effect;
    }

    /// <summary>
    /// Works out the effect of every routine of <paramref name="regions"/> and every label of
    /// <paramref name="labels"/>, and has each walk of <paramref name="walks"/> apply them.
    /// </summary>
    /// <param name="regions">Every routine of the program, by name.</param>
    /// <param name="walks">The walk through each routine's file, by the routine's name.</param>
    /// <param name="labels">
    /// The labels that are entry points of their own, each with the routine it is in and the index
    /// of the block it starts.
    /// </param>
    internal static StackEffects Solve(
        IReadOnlyDictionary<RoutineKey, FlowRegion> regions,
        IReadOnlyDictionary<RoutineKey, RegisterWalk> walks,
        IReadOnlyDictionary<RoutineKey, (RoutineKey Owner, int Start)> labels)
    {
        var entries = regions.Keys.ToDictionary(name => name, name => (Owner: name, Start: 0));
        foreach (var (label, (owner, start)) in labels)
            entries.TryAdd(label, (owner, start));
        var effects = new StackEffects(entries.Keys.ToDictionary(name => name, _ => StackEffect.NeverReturns));
        foreach (var walk in walks.Values.Distinct())
            walk.Effects = effects;

        // An entry's effect depends on the effects of everything its routine calls or hands
        // control to. Working the callees out first settles most entries in one pass, so the
        // entries are visited callees first and each is visited again only when one it depends
        // on changes.
        var dependents = new Dictionary<RoutineKey, HashSet<RoutineKey>>();
        var dependsOn = new Dictionary<RoutineKey, List<RoutineKey>>();
        foreach (var (name, (owner, _)) in entries)
        {
            var on = Targets(regions[owner], walks[owner]).Select(target => effects.KeyOf(target)).Distinct().ToList();
            dependsOn[name] = on;
            foreach (var target in on)
            {
                if (!dependents.TryGetValue(target, out var set))
                    dependents[target] = set = [];
                set.Add(name);
            }
        }

        var queue = new Queue<RoutineKey>(CalleesFirst(entries.Keys, dependsOn));
        var queued = queue.ToHashSet();
        var changed = new Dictionary<RoutineKey, int>();
        while (queue.TryDequeue(out var name))
        {
            queued.Remove(name);
            var (owner, start) = entries[name];
            var effect = Exits(walks[owner], regions[owner], start, effects);
            if (effect == effects.found[name])
                continue;
            changed[name] = changed.GetValueOrDefault(name) + 1;
            effects.found[name] = changed[name] > Changes ? StackEffect.Unknown : effect;
            foreach (var dependent in dependents.GetValueOrDefault(name) ?? [])
            {
                if (queued.Add(dependent))
                    queue.Enqueue(dependent);
            }
        }
        return effects;
    }

    /// <summary>
    /// Returns the name an effect is kept under for a target. A label that is not an entry point
    /// of its own is answered as part of the routine it is in.
    /// </summary>
    private RoutineKey KeyOf(Symbol target)
    {
        var key = RoutineKey.Of(target);
        return found.ContainsKey(key) || target is not { Kind: SymbolKind.Label, Routine: { } owner } ? key : RoutineKey.Of(owner);
    }

    /// <summary>
    /// Returns everything a routine's blocks call, or hand control to by a tail call, a branch, a
    /// <c>.next</c> or a <c>.fallthrough</c>.
    /// </summary>
    private static IEnumerable<Symbol> Targets(FlowRegion region, RegisterWalk walk) =>
        region.Blocks.SelectMany(block => block.Calls.Concat(walk.Leaves(block, region.Routine)));

    /// <summary>
    /// Returns <paramref name="names"/> ordered so that each comes after what it depends on,
    /// except within a set of names that depend on each other, which no order can settle.
    /// </summary>
    private static List<RoutineKey> CalleesFirst(
        IEnumerable<RoutineKey> names, IReadOnlyDictionary<RoutineKey, List<RoutineKey>> dependsOn)
    {
        var order = new List<RoutineKey>();
        var seen = new HashSet<RoutineKey>();
        foreach (var root in names)
        {
            if (!seen.Add(root))
                continue;
            var stack = new Stack<(RoutineKey Name, int Next)>();
            stack.Push((root, 0));
            while (stack.TryPop(out var top))
            {
                var on = dependsOn.GetValueOrDefault(top.Name) ?? [];
                if (top.Next < on.Count)
                {
                    stack.Push((top.Name, top.Next + 1));
                    if (dependsOn.ContainsKey(on[top.Next]) && seen.Add(on[top.Next]))
                        stack.Push((on[top.Next], 0));
                    continue;
                }
                order.Add(top.Name);
            }
        }
        return order;
    }

    /// <summary>
    /// Returns what the exits of <paramref name="region"/>'s routine leave, where it is entered at
    /// the block at <paramref name="start"/>, with <paramref name="effects"/> giving what each
    /// routine it calls or hands control to leaves so far.
    /// </summary>
    private static StackEffect Exits(RegisterWalk walk, FlowRegion region, int start, StackEffects effects)
    {
        // An interrupt handler has no caller, and a routine that never returns hands nothing back.
        if (region.Routine.Signature is { HasNoCaller: true } || !region.IsEntered || region.Blocks.Count == 0)
            return StackEffect.NeverReturns;
        var returnSize = (region.Routine.Signature ?? Signature.Default).ReturnSize;
        var blocks = region.Blocks;
        var solver = new Dataflow<Height>(
            blocks, (block, height) => Through(walk, block, height, effects), Height.Merge, block => ControlFlow.Onward(blocks, block));
        solver.Enter(start, new Height(returnSize, 0, 0, true));
        var effect = StackEffect.NeverReturns;
        foreach (var block in blocks)
        {
            if (solver.Reached[block.Index] is not { } reached)
                continue;
            effect = StackEffect.Join(effect, Exit(block, Through(walk, block, reached, effects).Bytes));
            if (effect.Kind == StackEffectKind.Unknown)
                return effect;
        }
        return effect;

        StackEffect Exit(BasicBlock block, int? height)
        {
            var last = block.Steps.Count > 0 ? block.Steps[^1].Statement as InstructionStatementSyntax : null;
            switch (block.End)
            {
                case BlockEnd.Return when last?.MnemonicKind == MnemonicKind.Rti:
                case BlockEnd.Stop or BlockEnd.CallNeverReturns:
                    return StackEffect.NeverReturns;
                case BlockEnd.Return:
                    return height is { } h ? StackEffect.Leaving(h - returnSize) : StackEffect.Unknown;
                case BlockEnd.Elsewhere:
                case BlockEnd.TailCall when block.CallsUnknown:
                    return StackEffect.Unknown;
            }

            // A return used as a jump pulls the address it jumps to before control arrives there.
            if (last?.MnemonicKind is MnemonicKind.Rts or MnemonicKind.Rtl && block.Next is not null)
                height -= last.MnemonicKind == MnemonicKind.Rts ? 2 : 3;

            // Control arrives at a routine handed it as at a call, with the top bytes taken for
            // its return address, and what that routine leaves is counted from there.
            var handed = walk.Leaves(block, region.Routine);
            if (block.End == BlockEnd.TailCall)
                handed = handed.Concat(block.Calls);
            var leaves = StackEffect.NeverReturns;
            foreach (var target in handed.Distinct())
            {
                var into = effects.Of(target);
                if (into.Kind == StackEffectKind.NeverReturns)
                    continue;
                var size = (RegisterWalk.Owner(target)?.Signature ?? Signature.Default).ReturnSize;
                leaves = StackEffect.Join(leaves,
                    into.Kind == StackEffectKind.Unknown || height is null
                        ? StackEffect.Unknown
                        : StackEffect.Leaving(height.Value - size + into.Bytes));
            }
            return leaves;
        }
    }

    /// <summary>
    /// Returns the height after <paramref name="block"/>, from the <paramref name="height"/> that
    /// reaches it, with <paramref name="effects"/> giving what the call the block ends with leaves.
    /// </summary>
    private static Height Through(RegisterWalk walk, BasicBlock block, Height height, StackEffects effects)
    {
        foreach (var step in block.Steps)
        {
            if (!height.IsKnown)
                return height;
            if (step.Statement is not InstructionStatementSyntax statement)
                continue;

            // A call pushes the return address its routine's return pulls, which the routine's
            // effect accounts for, and a return or an interrupt is where the path leaves.
            var mnemonic = statement.MnemonicKind;
            if (Instructions.IsCall(mnemonic) || mnemonic is MnemonicKind.Rts or MnemonicKind.Rtl or MnemonicKind.Rti
                or MnemonicKind.Brk or MnemonicKind.Cop)
            {
                continue;
            }
            if (RegisterEffects.SetsStackPointer(mnemonic))
                return Height.Unknown;
            var facts = Instructions.Facts(mnemonic);
            if (facts.Pushes is { } push)
                height = height.Moved(push, walk.Width(step, push), 1);
            else if (facts.Pulls is { } pull)
                height = height.Moved(pull, walk.Width(step, pull), -1);
        }
        if (!block.EndsInCall || !height.IsKnown)
            return height;

        // A relative call pushes its return address with `per`, and `phk` where it is far, which
        // the routine's return pulls.
        if (block.Steps.Count > 0 && walk.Flow.RelativeCallAt(block.Steps[^1]) is { } relative)
            height = height with { Whole = height.Whole - relative.Pushed };
        var effect = effects.OfCallIn(block);
        return effect.KeepsTheStack ? height
            : effect.Kind == StackEffectKind.Unknown ? Height.Unknown
            : height with { Whole = height.Whole + effect.Bytes };
    }

    /// <summary>
    /// Represents how many bytes a routine's stack holds above its caller's, as the walk that works
    /// out effects counts them. On the 65816 a push of a register whose width is as the routine was
    /// entered with is not a known number of bytes, so it is counted apart.
    /// </summary>
    /// <param name="Whole">The bytes whose number is known.</param>
    /// <param name="Accumulator">How many pushes of the accumulator at its entry width are on the stack.</param>
    /// <param name="Index">How many pushes of an index register at its entry width are on the stack.</param>
    /// <param name="IsKnown">Whether anything is known about the height at all.</param>
    private sealed record Height(int Whole, int Accumulator, int Index, bool IsKnown)
    {
        /// <summary>Gets a height about which nothing is known.</summary>
        public static Height Unknown { get; } = new(0, 0, 0, false);

        /// <summary>Gets the number of bytes, or null where it is not known.</summary>
        public int? Bytes => IsKnown && Accumulator == 0 && Index == 0 ? Whole : null;

        /// <summary>
        /// Returns what is known where <paramref name="arriving"/> meets the height
        /// <paramref name="known"/> already at a block, which is nothing where the two differ.
        /// </summary>
        public static Height Merge(Height? known, Height arriving) =>
            known is null || known == arriving ? arriving : Unknown;

        /// <summary>
        /// Returns this height after a push, where <paramref name="sign"/> is 1, or a pull, where it
        /// is -1, of <paramref name="size"/>, moving a register <paramref name="width"/> wide.
        /// </summary>
        public Height Moved(PushSize size, Width width, int sign) => (size, width) switch
        {
            (PushSize.OneByte, _) => this with { Whole = Whole + sign },
            (PushSize.TwoBytes, _) => this with { Whole = Whole + (2 * sign) },
            (_, Width.Eight) => this with { Whole = Whole + sign },
            (_, Width.Sixteen) => this with { Whole = Whole + (2 * sign) },
            (PushSize.Accumulator, Width.Unchanged) => this with { Accumulator = Accumulator + sign },
            (PushSize.Index, Width.Unchanged) => this with { Index = Index + sign },
            _ => Unknown,
        };
    }
}
