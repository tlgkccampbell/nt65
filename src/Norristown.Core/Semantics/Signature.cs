using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// The processor state a routine declares at entry and, after <c>-&gt;</c>, at exit, and
/// whether it is called near or far. Signatures are declared, never inferred: that is what
/// keeps the analysis inside one routine and a file's interface free of its bodies.
/// </summary>
/// <param name="Entry">The state the routine assumes when it is called.</param>
/// <param name="Exit">The state it returns with. A part the exit does not give is the entry's.</param>
/// <param name="IsFar">Whether it is entered by <c>jsl</c> and left by <c>rtl</c>.</param>
/// <param name="Inline">
/// The <c>inline n</c> or <c>inline .strz</c> item, for a routine that returns past data
/// written after each call; null for every other.
/// </param>
public sealed record Signature(ProcessorState Entry, ProcessorState Exit, bool IsFar, StateItem? Inline = null)
{
    // The parts of the state a bare `?` stands for, each of them unknown.
    private static readonly StatePart[] trackedParts =
        [StatePart.A, StatePart.Index, StatePart.E, StatePart.DirectPage, StatePart.DataBank];

    // What the signature was read from, and whether as a macro's, so it can be read again once
    // the signature sets it names are resolved and the values of its `dp = e`, `dbr = e` and
    // `args n` items are known.
    private SyntaxNode? syntax;
    private bool forMacro;

    /// <summary>What a routine that writes no signature declares: <c>a*, i*, native, near</c>.</summary>
    public static Signature Default { get; } = new(ProcessorState.Default, ProcessorState.Default, false);

    /// <summary>What a macro that writes no signature declares: that it assumes and changes nothing.</summary>
    public static Signature Unchanged { get; } = new(
        new ProcessorState(Width.Unchanged, Width.Unchanged, ProcessorMode.Unchanged),
        new ProcessorState(Width.Unchanged, Width.Unchanged, ProcessorMode.Unchanged),
        false);

    /// <summary>
    /// Whether it is an interrupt handler: entered by the processor from anywhere, knowing
    /// nothing but perhaps its mode, and left by <c>rti</c>. It is neither near nor far.
    /// </summary>
    public bool IsInterrupt { get; init; }

    /// <summary>Whether it never returns, <c>noreturn</c>, so a call to it is where a path ends.</summary>
    public bool NeverReturns { get; init; }

    /// <summary>How many bytes the caller pushes before a call, <c>args n</c>; 0 for a routine that says nothing.</summary>
    public int Arguments { get; init; }

    /// <summary>
    /// The registers it hands back as it was entered with them, <c>keeps a, x</c>; none for a
    /// routine that promises nothing. A routine with a body is checked against it; one without
    /// is taken at its word, which is the only way to know anything about a body that is not here.
    /// </summary>
    public Layout.Registers Keeps { get; init; }

    /// <summary>How the routine is called and left, as the item that says so.</summary>
    public string Distance => IsInterrupt ? "interrupt" : IsFar ? "far" : "near";

    /// <summary>
    /// Whether the routine wrote a signature with at least one item in it, rather than taking the
    /// default or writing an empty <c>proc()</c>. A routine with no body is held to this on the
    /// 65816, because nothing else says what a caller must hold to. A signature of nothing but
    /// <c>keeps</c> does not answer it: which registers come back says nothing about the widths.
    /// </summary>
    public bool DeclaresState =>
        syntax is not null && StateItem.Read(syntax).Any(item => item.Part != StatePart.Keeps);

    /// <summary>
    /// Whether nothing waits for the routine to return: it never does, or it returns by
    /// <c>rti</c> to wherever the interrupt came. A jump from such a routine is checked against
    /// the target's entry only.
    /// </summary>
    public bool HasNoCaller => IsInterrupt || NeverReturns;

    /// <summary>The signature as it would be written in full.</summary>
    public override string ToString()
    {
        var entry = IsInterrupt ? $"interrupt, {ProcessorState.Spell(Entry.E)}" : $"{Entry}, {Distance}";
        if (Arguments > 0)
            entry += $", args {Arguments}";
        if (NeverReturns)
            entry += ", noreturn";
        if (Keeps != Layout.Registers.None)
            entry += $", keeps {Layout.RegisterEffects.Spell(Keeps).ToLowerInvariant()}";
        return entry + (NeverReturns || IsInterrupt || Exit == Entry ? "" : $" -> {Exit}");
    }

    /// <summary>
    /// What a proc, an extern proc or an import writes, as far as it can be read from its
    /// syntax alone: the signature sets it names count for nothing, and the values of its items
    /// are unknown, until <see cref="Resolved"/> reads it again. <paramref name="syntax"/> is
    /// the <c>: entry -&gt; exit</c> of a proc or the <c>proc(...)</c> of an import, or null
    /// where nothing was written.
    /// </summary>
    public static Signature Read(SyntaxNode? syntax) => Read(syntax, forMacro: false, null, null, (_, _) => { });

    /// <summary>
    /// What a macro writes. A macro's items default to <c>*</c>, because a macro assumes and
    /// changes nothing it does not declare, and <c>near</c>, <c>far</c>, <c>inline</c>,
    /// <c>args</c>, <c>interrupt</c> and <c>noreturn</c> describe how a routine is called,
    /// entered or left, which a macro is not.
    /// </summary>
    public static Signature ReadMacro(SyntaxNode? syntax) => Read(syntax, forMacro: true, null, null, (_, _) => { });

    /// <summary>
    /// Reports what is wrong with the items a signature set declares, reading them as a proc's
    /// entry would: once here, where the set is declared, rather than at every signature that
    /// names it.
    /// </summary>
    public static void CheckSet(
        Symbol set, Func<SyntaxNode, long?> valueOf, Func<SyntaxNode, Symbol?> setOf, Action<TextSpan, string> report)
    {
        if (set.Definition is not { Parent: { } declaration } list)
            return;
        foreach (var item in StateItem.Read(list))
        {
            if (item.Part == StatePart.Set && setOf(item.SetName!) is { Kind: SymbolKind.SignatureSet } named
                && Reaches(named, set, setOf, []))
            {
                report(item.Node.Span, $"`{set.Name}` names `{item.Text}`, which stands for `{set.Name}` again: "
                    + "a signature set cannot stand for itself");
            }
        }
        Read(declaration, forMacro: false, valueOf, setOf, report);
    }

    /// <summary>
    /// The signature with the signature sets it names read, and the values of its <c>dp = e</c>,
    /// <c>dbr = e</c> and <c>args n</c> items, which are expressions and so can be worked out
    /// only once the program's constants are. What is wrong with it is reported to
    /// <paramref name="report"/>.
    /// </summary>
    public Signature Resolved(
        Func<SyntaxNode, long?> valueOf, Func<SyntaxNode, Symbol?> setOf, Action<TextSpan, string> report) =>
        syntax is null ? this : Read(syntax, forMacro, valueOf, setOf, report);

    /// <summary>Whether the sets <paramref name="from"/> names come, however indirectly, to <paramref name="to"/>.</summary>
    private static bool Reaches(Symbol from, Symbol to, Func<SyntaxNode, Symbol?> setOf, HashSet<Symbol> seen)
    {
        if (from == to)
            return true;
        if (!seen.Add(from) || from.Definition is not { } list)
            return false;
        return StateItem.Read(list).Any(item => item.Part == StatePart.Set
            && setOf(item.SetName!) is { Kind: SymbolKind.SignatureSet } named
            && Reaches(named, to, setOf, seen));
    }

    private static Signature Read(
        SyntaxNode? syntax, bool forMacro,
        Func<SyntaxNode, long?>? valueOf, Func<SyntaxNode, Symbol?>? setOf, Action<TextSpan, string> report)
    {
        var defaults = forMacro ? Unchanged.Entry : ProcessorState.Default;
        if (syntax is null)
            return forMacro ? Unchanged : Default;

        var arrow = syntax.ChildTokens.FirstOrDefault(token => token.Kind == SyntaxKind.Arrow);
        var lists = syntax.ChildNodes.Where(node => node.Kind == SyntaxKind.StateList).ToList();
        var entryList = lists.FirstOrDefault(list => arrow.Parent is null || list.Position < arrow.Position);
        var exitList = arrow.Parent is null ? null : lists.FirstOrDefault(list => list.Position > arrow.Position);

        // The items the signature sets it names give, which are reported where each set is declared.
        var fromSets = new HashSet<(SyntaxTree Tree, int Position)>();
        var entry = Take(entryList, isExit: false);
        var exit = Take(exitList, isExit: true);

        var arguments = 0;
        if (entry.Arguments is { Expression: { } count } args && valueOf is not null && Here(args))
        {
            if (valueOf(count) is not { } bytes)
                report(count.Span, $"`{args.Text}` needs a constant: it counts the bytes the caller pushes");
            else if (bytes < 0 || bytes > 0xffff)
                report(count.Span, $"`{args.Text}` is out of range: it counts the bytes the caller pushes");
            else
                arguments = (int)bytes;
        }

        if (entry.Interrupt is not null)
            return Interrupt();

        var entryState = new ProcessorState(
            entry.A?.Width ?? defaults.A, entry.Index?.Width ?? defaults.Index, entry.E?.Mode ?? defaults.E,
            ValueOf(entry.D, 0xffff) ?? defaults.D, ValueOf(entry.B, 0xff) ?? defaults.B);
        CheckEmulation(entry, entryState);
        entryState = Pinned(entryState);

        // A routine that never returns has no exit state to declare.
        if (entry.NoReturn is not null)
        {
            if (exitList is not null)
                report(exitList.Span, "a routine that says `noreturn` never returns, and declares nothing after `->`");
            return Made(entryState, entryState) with { NeverReturns = true };
        }

        // An exit that names a 16-bit width and not the mode is in native mode, the only one
        // that width can hold in, whatever the entry's mode.
        var exitMode = exit.E?.Mode
            ?? (entryState.E == ProcessorMode.Emulation && (exit.A?.Width == Width.Sixteen || exit.Index?.Width == Width.Sixteen)
                ? ProcessorMode.Native
                : entryState.E);
        var exitState = new ProcessorState(
            exit.A?.Width ?? entryState.A, exit.Index?.Width ?? entryState.Index, exitMode,
            ValueOf(exit.D, 0xffff) ?? entryState.D, ValueOf(exit.B, 0xff) ?? entryState.B);

        // An exit that cannot be what it says is reported once, and read as the entry's, so
        // what uses the signature does not report the same mistake again.
        if (!Kept(exit.A, entryState.A == Width.Unchanged))
            exitState = exitState with { A = entryState.A };
        if (!Kept(exit.Index, entryState.Index == Width.Unchanged))
            exitState = exitState with { Index = entryState.Index };
        if (!Kept(exit.E, entryState.E == ProcessorMode.Unchanged))
            exitState = exitState with { E = entryState.E };
        if (!Kept(exit.D, entryState.D.Kind == StateValueKind.Unchanged))
            exitState = exitState with { D = entryState.D };
        if (!Kept(exit.B, entryState.B.Kind == StateValueKind.Unchanged))
            exitState = exitState with { B = entryState.B };
        CheckEmulation(exit, exitState);
        return Made(entryState, Pinned(exitState));

        Signature Made(ProcessorState entered, ProcessorState exited) =>
            new(entered, exited, entry.Far?.IsFar ?? false, entry.Inline)
            {
                Arguments = arguments,
                Keeps = Promised(entry),
                syntax = syntax,
                forMacro = forMacro,
            };

        // The registers the list promises. A list that writes `keeps` itself says which they
        // are; one that writes none takes what the signature set it names gives.
        static Layout.Registers Promised(Parts parts)
        {
            var own = parts.Keeps.Where(kept => !kept.FromSet).ToList();
            return (own.Count > 0 ? own : parts.Keeps)
                .Aggregate(Layout.Registers.None, (all, kept) => all | kept.Item.Registers);
        }

        // An interrupt handler is entered from anywhere, so all it may say is which mode the
        // processor is in, and it leaves by `rti`, so it says nothing after `->`.
        Signature Interrupt()
        {
            foreach (var other in new[] { entry.A, entry.Index, entry.D, entry.B })
            {
                if (other is { } given)
                {
                    report(At(entry, given), $"`{given.Text}`: an interrupt handler is entered from anywhere, and "
                        + "assumes nothing but its mode: `interrupt, native` or `interrupt, emu`");
                }
            }
            foreach (var other in new[] { entry.Far, entry.Inline, entry.Arguments })
            {
                if (other is { } given)
                {
                    report(At(entry, given), $"`{given.Text}` describes how a routine is called, and an interrupt "
                        + "handler is entered by the processor, which makes it neither near nor far");
                }
            }
            if (entry.NoReturn is { } never)
            {
                report(At(entry, never), $"`{never.Text}`: an interrupt handler leaves by `rti`, "
                    + "and never returns to a caller in any case");
            }
            if (entry.E is { IsUnchanged: true } kept)
                report(At(entry, kept), $"`{kept.Text}`: an interrupt handler says which mode it is entered in, or nothing");
            if (exitList is not null)
                report(exitList.Span, "an interrupt handler leaves by `rti`, and declares nothing after `->`");

            var mode = entry.E is { IsUnchanged: false } stated ? stated.Mode : ProcessorMode.Unknown;
            var state = Pinned(new ProcessorState(Width.Unknown, Width.Unknown, mode, StateValue.Unknown, StateValue.Unknown));
            return new Signature(state, state, false)
            {
                IsInterrupt = true,
                Keeps = Promised(entry),
                syntax = syntax,
                forMacro = forMacro,
            };
        }

        // Whether an item is written in the signature itself rather than given by a set it names.
        bool Here(StateItem item) => !fromSets.Contains((item.Node.Tree, item.Node.Position));

        // Where a mistake about an item is reported: at the item, or at the set that gave it.
        TextSpan At(Parts parts, StateItem item) =>
            Here(item) || parts.SetReference is not { } reference ? item.Node.Span : reference.Span;

        // `dp = e` is worth e once the constants are known, and unknown before; `dp?` is
        // unknown and `dp*` unchanged. A value a set gives is reported where the set is declared.
        StateValue? ValueOf(StateItem? item, long largest)
        {
            if (item is not { } given)
                return null;
            if (given.IsUnchanged)
                return StateValue.Unchanged;
            if (given.Expression is not { } expression || valueOf is null)
                return StateValue.Unknown;
            if (valueOf(expression) is not { } value)
            {
                if (Here(given))
                    report(expression.Span, $"`{given.Text}` needs a constant: the analysis follows D and B by value");
                return StateValue.Unknown;
            }
            if (value < 0 || value > largest)
            {
                if (Here(given))
                {
                    report(expression.Span, $"`{given.Text}` is out of range: "
                        + (largest == 0xff ? "a bank is one byte" : "the direct page is a 16-bit address"));
                }
                return StateValue.Unknown;
            }
            return StateValue.Of(value);
        }

        // The items of one list by part. A signature set comes first, and what the list writes
        // after it takes the place of what the set gives for the same part.
        Parts Take(SyntaxNode? list, bool isExit)
        {
            var parts = new Parts();
            var first = true;
            foreach (var item in StateItem.Read(list))
            {
                if (item.Part != StatePart.Set)
                {
                    Assign(parts, item, isExit, fromSet: false);
                    first = false;
                    continue;
                }
                if (!first)
                {
                    report(item.Node.Span, $"`{item.Text}` is a signature set, and comes first in its list: "
                        + "the items after it change what it gives");
                    continue;
                }
                first = false;
                parts.SetReference = item.Node;
                foreach (var setItem in Expand(item, []))
                {
                    fromSets.Add((setItem.Node.Tree, setItem.Node.Position));

                    // An exit, and a macro, take a set's state and not how a routine is called or entered.
                    if ((isExit || forMacro) && setItem.Part is StatePart.Distance or StatePart.Inline
                        or StatePart.Arguments or StatePart.Interrupt or StatePart.NoReturn or StatePart.Keeps)
                    {
                        continue;
                    }
                    Assign(parts, setItem, isExit, fromSet: true);
                }
            }
            return parts;
        }

        // The items a set stands for, the sets it names spread out in place. Before the program's
        // names are resolved a set stands for nothing.
        List<StateItem> Expand(StateItem reference, HashSet<Symbol> seen)
        {
            var items = new List<StateItem>();
            if (setOf?.Invoke(reference.SetName!) is not { } set)
                return items;
            if (set.Kind != SymbolKind.SignatureSet)
            {
                if (Here(reference))
                {
                    report(reference.Node.Span, $"`{reference.Text}` is {set.KindPhrase}, and a name among a "
                        + "signature's items is a signature set");
                }
                return items;
            }
            if (!seen.Add(set) || set.Definition is not { } list)
                return items;
            foreach (var item in StateItem.Read(list))
            {
                if (item.Part == StatePart.Set)
                    items.AddRange(Expand(item, seen));
                else
                    items.Add(item);
            }
            return items;
        }

        void Assign(Parts parts, StateItem item, bool isExit, bool fromSet)
        {
            switch (item.Part)
            {
                case StatePart.AllUnknown:
                    foreach (var part in trackedParts)
                        Assign(parts, item with { Part = part }, isExit, fromSet: true);
                    break;
                case StatePart.A:
                    parts.A = Once(parts, parts.A, item, fromSet);
                    break;
                case StatePart.Index:
                    parts.Index = Once(parts, parts.Index, item, fromSet);
                    break;
                case StatePart.E:
                    parts.E = Once(parts, parts.E, item, fromSet);
                    break;
                case StatePart.DirectPage:
                    parts.D = Once(parts, parts.D, item, fromSet);
                    break;
                case StatePart.DataBank:
                    parts.B = Once(parts, parts.B, item, fromSet);
                    break;

                case StatePart.Keeps when forMacro:
                    report(item.Node.Span, $"`{item.Text}` is about a routine from entry to exit, and a macro is "
                        + "expanded into one: what its body keeps is part of what that routine keeps");
                    break;
                case StatePart.Keeps when isExit:
                    report(item.Node.Span,
                        $"`{item.Text}` is about a routine from entry to exit, and belongs before `->`");
                    break;
                case StatePart.Keeps:
                    if (item.Registers == Layout.Registers.None)
                        break;
                    parts.Keeps.Add((item, fromSet));
                    break;

                case StatePart.NoReturn when forMacro:
                    report(item.Node.Span, "`noreturn` says a routine never returns, and a macro is expanded, "
                        + "ending where its body does");
                    break;
                case StatePart.Distance or StatePart.Inline or StatePart.Arguments or StatePart.Interrupt when forMacro:
                    report(item.Node.Span, $"`{item.Text}` describes how a routine is called, and a macro is expanded");
                    break;
                case StatePart.Distance or StatePart.Inline or StatePart.Arguments or StatePart.Interrupt
                    or StatePart.NoReturn when isExit:
                    report(item.Node.Span,
                        $"`{item.Text}` describes how a routine is called, entered or left, and belongs before `->`");
                    break;
                case StatePart.NoReturn:
                    parts.NoReturn = Once(parts, parts.NoReturn, item, fromSet);
                    break;
                case StatePart.Inline:
                    parts.Inline = Once(parts, parts.Inline, item, fromSet);
                    break;
                case StatePart.Arguments:
                    parts.Arguments = Once(parts, parts.Arguments, item, fromSet);
                    break;
                case StatePart.Interrupt:
                    parts.Interrupt = Once(parts, parts.Interrupt, item, fromSet);
                    break;
                case StatePart.Distance:
                    if (!fromSet && parts.Written.Contains(StatePart.Distance) && parts.Far is { } said && said.IsFar != item.IsFar)
                    {
                        report(item.Node.Span,
                            $"`{item.Text}` disagrees with `{(said.IsFar ? "far" : "near")}`: a routine is called one way");
                    }
                    if (!fromSet)
                        parts.Written.Add(StatePart.Distance);
                    parts.Far = item;
                    break;
                default:
                    break;
            }
        }

        // Two items a list writes for one part are a mistake; an item written after a set takes
        // the place of what the set gives.
        StateItem? Once(Parts parts, StateItem? earlier, StateItem item, bool fromSet)
        {
            if (fromSet)
                return item;
            if (earlier is { } first && !parts.Written.Add(item.Part))
                report(item.Node.Span, $"`{first.Text}` and `{item.Text}` both describe the same part of the state");
            parts.Written.Add(item.Part);
            return item;
        }

        // A routine can hand back unchanged only what it assumed nothing about.
        bool Kept(StateItem? exitItem, bool keptAtEntry)
        {
            if (exitItem is not { IsUnchanged: true } kept || keptAtEntry)
                return true;
            report(At(exit, kept), $"`{kept.Text}` after `->` needs `{kept.Text}` at entry too: a routine "
                + "hands back unchanged only what it assumed nothing about");
            return false;
        }

        // Emulation mode pins both widths at 8 bits, so `emu` says that too, as it does in `.state`.
        static ProcessorState Pinned(ProcessorState state) =>
            state.E == ProcessorMode.Emulation ? state with { A = Width.Eight, Index = Width.Eight } : state;

        void CheckEmulation(Parts parts, ProcessorState state)
        {
            if (state.E != ProcessorMode.Emulation)
                return;
            foreach (var wide in new[] { parts.A, parts.Index })
            {
                if (wide is { Width: Width.Sixteen } item)
                    report(At(parts, item), $"`{item.Text}` cannot hold in emulation mode, where both widths are 8 bits");
            }
        }
    }

    /// <summary>The items one list gives, by part, and the signature set it names, if it names one.</summary>
    private sealed class Parts
    {
        public StateItem? A;
        public StateItem? Index;
        public StateItem? E;
        public StateItem? D;
        public StateItem? B;
        public StateItem? Far;
        public StateItem? Inline;
        public StateItem? Arguments;
        public StateItem? Interrupt;
        public StateItem? NoReturn;
        public SyntaxNode? SetReference;

        // Every `keeps` the list gives, and whether a signature set gave it. A list may write
        // more than one, and what it writes itself takes the place of what a set gives.
        public readonly List<(StateItem Item, bool FromSet)> Keeps = [];

        // The parts the list writes itself, rather than takes from the set.
        public readonly HashSet<StatePart> Written = [];
    }
}
