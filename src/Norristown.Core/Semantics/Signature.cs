using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Represents the processor state a routine declares at entry and, after <c>-&gt;</c>, at exit,
/// and whether it is called near or far. Signatures are declared, never inferred. This keeps
/// the analysis within one routine and keeps a file's interface independent of its bodies.
/// </summary>
/// <param name="Entry">The state the routine assumes when it is called.</param>
/// <param name="Exit">
/// The state the routine returns with. A part the exit does not give is taken from the entry.
/// </param>
/// <param name="IsFar">Whether the routine is entered by <c>jsl</c> and left by <c>rtl</c>.</param>
/// <param name="Inline">
/// The <c>inline n</c> or <c>inline .strz</c> item, for a routine that returns past data placed
/// after each call; null for every other routine.
/// </param>
public sealed record Signature(ProcessorState Entry, ProcessorState Exit, bool IsFar, StateItem? Inline = null)
{
    // The parts of the state that a bare `?` makes unknown.
    private static readonly StatePart[] trackedParts =
        [StatePart.A, StatePart.Index, StatePart.E, StatePart.DirectPage, StatePart.DataBank];

    // The syntax the signature was read from, and whether it was read as a macro's. These let it
    // be read again once the signature sets it names are resolved and the values of its
    // `dp = e`, `dbr = e` and `args n` items are known.
    private SyntaxNode? syntax;
    private bool forMacro;

    /// <summary>
    /// Gets the signature of a routine that declares none, which is <c>a*, i*, native, near</c>.
    /// </summary>
    public static Signature Default { get; } = new(ProcessorState.Default, ProcessorState.Default, false);

    /// <summary>
    /// Gets the signature of a macro that declares none, which states that it assumes and changes
    /// nothing.
    /// </summary>
    public static Signature Unchanged { get; } = new(
        new ProcessorState(Width.Unchanged, Width.Unchanged, ProcessorMode.Unchanged),
        new ProcessorState(Width.Unchanged, Width.Unchanged, ProcessorMode.Unchanged),
        false);

    /// <summary>
    /// Gets a value indicating whether the routine is an interrupt handler. A handler is entered
    /// by the processor from anywhere, knowing nothing except perhaps its mode, and is left by
    /// <c>rti</c>. It is neither near nor far.
    /// </summary>
    public bool IsInterrupt { get; init; }

    /// <summary>
    /// Gets a value indicating whether the routine never returns (<c>noreturn</c>), so that a path
    /// ends at a call to it.
    /// </summary>
    public bool NeverReturns { get; init; }

    /// <summary>
    /// Gets the number of bytes the caller pushes before a call (<c>args n</c>), or 0 for a
    /// routine that declares none.
    /// </summary>
    public int Arguments { get; init; }

    /// <summary>
    /// Gets the registers the routine returns with the values they had at entry
    /// (<c>keeps a, x</c>), or none for a routine that promises nothing. A routine with a body is
    /// checked against this promise. A routine without a body is trusted, because the promise is
    /// the only thing known about a body that is not in the program.
    /// </summary>
    public Processor.Registers Keeps { get; init; }

    /// <summary>Gets how the routine is called and left, as the signature item that declares it.</summary>
    public string Distance => IsInterrupt ? "interrupt" : IsFar ? "far" : "near";

    /// <summary>
    /// Gets a value indicating whether the routine declares a signature with at least one item,
    /// rather than taking the default or declaring an empty <c>proc()</c>. On the 65816, a routine
    /// with no body must do so, because nothing else states what a caller must hold to. A
    /// signature of only <c>keeps</c> does not count, because which registers are preserved says
    /// nothing about the widths.
    /// </summary>
    public bool DeclaresState =>
        syntax is not null && StateItem.Read(syntax).Any(item => item.Part != StatePart.Keeps);

    /// <summary>
    /// Gets a value indicating whether no caller waits for the routine to return, because it never
    /// returns or returns by <c>rti</c> to wherever the interrupt came from. A jump from such a
    /// routine is checked only against the target's entry.
    /// </summary>
    public bool HasNoCaller => IsInterrupt || NeverReturns;

    /// <summary>Returns the signature formatted in full.</summary>
    public override string ToString()
    {
        var entry = IsInterrupt ? $"interrupt, {ProcessorState.Format(Entry.E)}" : $"{Entry}, {Distance}";
        if (Arguments > 0)
            entry += $", args {Arguments}";
        if (NeverReturns)
            entry += ", noreturn";
        if (Keeps != Processor.Registers.None)
            entry += $", keeps {Processor.RegisterEffects.Format(Keeps).ToLowerInvariant()}";
        return entry + (NeverReturns || IsInterrupt || Exit == Entry ? "" : $" -> {Exit}");
    }

    /// <summary>
    /// Returns whether two signatures declare the same thing. The syntax each was read from is
    /// not compared, so a routine and another name for it can be checked against each other.
    /// An <c>inline</c> item is compared by its text, because its count is evaluated where the
    /// routine is called.
    /// </summary>
    public bool Equals(Signature? other) =>
        other is not null
        && Entry == other.Entry && Exit == other.Exit && IsFar == other.IsFar
        && Inline?.Text == other.Inline?.Text && IsInterrupt == other.IsInterrupt
        && NeverReturns == other.NeverReturns && Arguments == other.Arguments && Keeps == other.Keeps;

    /// <summary>Returns a hash code over the same parts that <see cref="Equals(Signature?)"/> compares.</summary>
    public override int GetHashCode() =>
        HashCode.Combine(Entry, Exit, IsFar, Inline?.Text, IsInterrupt, NeverReturns, Arguments, Keeps);

    /// <summary>
    /// Reads the signature a proc, an extern proc or an import declares, as far as it can be read
    /// from its syntax alone. The signature sets it names are ignored, and the values of its items
    /// are unknown, until <see cref="Resolved"/> reads it again. <paramref name="syntax"/> is the
    /// <c>: entry -&gt; exit</c> of a proc or the <c>proc(...)</c> of an import, or null when no
    /// signature was given.
    /// </summary>
    public static Signature Read(SyntaxNode? syntax) => Read(syntax, forMacro: false, null, null, (_, _) => { });

    /// <summary>
    /// Reads the signature a macro declares. A macro's items default to <c>*</c>, because a macro
    /// assumes and changes nothing it does not declare. <c>near</c>, <c>far</c>, <c>inline</c>,
    /// <c>args</c>, <c>interrupt</c> and <c>noreturn</c> are not allowed, because they describe
    /// how a routine is called, entered or left, and a macro is none of these.
    /// </summary>
    public static Signature ReadMacro(SyntaxNode? syntax) => Read(syntax, forMacro: true, null, null, (_, _) => { });

    /// <summary>
    /// Reports the problems with the items a signature set declares, reading them as a proc's
    /// entry would. They are reported once, here where the set is declared, rather than at every
    /// signature that names the set.
    /// </summary>
    public static void CheckSet(
        Symbol set, Func<ExpressionSyntax, long?> valueOf, Func<NameExpressionSyntax, Symbol?> setOf, Action<TextSpan, DiagnosticMessage> report)
    {
        if (set.Definition is not { Parent: { } declaration } list)
            return;
        foreach (var item in StateItem.Read(list))
        {
            if (item.Part == StatePart.Set && setOf(item.SetName!) is { Kind: SymbolKind.SignatureSet } named
                && Reaches(named, set, setOf, []))
            {
                report(item.Node.Span, Catalogue.SignatureSetSelfReference.Message(set.Name, item.Text, set.Name));
            }
        }
        Read(declaration, forMacro: false, valueOf, setOf, report);
    }

    /// <summary>
    /// Returns the signature read again with the signature sets it names and the values of its
    /// <c>dp = e</c>, <c>dbr = e</c> and <c>args n</c> items. Those items are expressions, so they
    /// can be evaluated only once the program's constants have been. Problems are reported to
    /// <paramref name="report"/>.
    /// </summary>
    public Signature Resolved(
        Func<ExpressionSyntax, long?> valueOf, Func<NameExpressionSyntax, Symbol?> setOf, Action<TextSpan, DiagnosticMessage> report) =>
        syntax is null ? this : Read(syntax, forMacro, valueOf, setOf, report);

    /// <summary>
    /// Returns a value indicating whether the sets <paramref name="from"/> names lead, directly or
    /// indirectly, to <paramref name="to"/>.
    /// </summary>
    private static bool Reaches(Symbol from, Symbol to, Func<NameExpressionSyntax, Symbol?> setOf, HashSet<Symbol> seen)
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
        Func<ExpressionSyntax, long?>? valueOf, Func<NameExpressionSyntax, Symbol?>? setOf, Action<TextSpan, DiagnosticMessage> report) =>
        syntax is null
            ? forMacro ? Unchanged : Default
            : new Reader(syntax, forMacro, valueOf, setOf, report).Read();

    /// <summary>
    /// Holds the items one list gives, by part, and the signature set it names, if any.
    /// </summary>
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
        public StateItemSyntax? SetReference;

        // Every `keeps` the list gives, and whether a signature set gave it. A list may contain
        // more than one, and its own replace what a set gives.
        public readonly List<(StateItem Item, bool FromSet)> Keeps = [];

        // The parts the list gives itself, rather than taking from the set.
        public readonly HashSet<StatePart> Given = [];
    }

    /// <summary>
    /// Reads one signature from its syntax. An instance is used for one reading, and holds the
    /// items each list gives and which of them came from the signature sets the lists name.
    /// </summary>
    /// <param name="syntax">The signature's syntax.</param>
    /// <param name="forMacro">Whether the signature is read as a macro's.</param>
    /// <param name="valueOf">
    /// Evaluates an item's expression, or is null before the program's constants are known.
    /// </param>
    /// <param name="setOf">
    /// Resolves the name of a signature set, or is null before the program's names are resolved.
    /// </param>
    /// <param name="report">Receives the problems the reading finds.</param>
    private sealed class Reader(
        SyntaxNode syntax, bool forMacro,
        Func<ExpressionSyntax, long?>? valueOf, Func<NameExpressionSyntax, Symbol?>? setOf, Action<TextSpan, DiagnosticMessage> report)
    {
        // The items that come from the signature sets this signature names. Problems with them
        // are reported where each set is declared.
        private readonly HashSet<(SyntaxTree Tree, int Position)> fromSets = [];

        // The items the entry and the exit give, by part.
        private readonly Parts entry = new();
        private readonly Parts exit = new();

        // The list after `->`, or null when the signature declares no exit.
        private StateListSyntax? exitList;

        // The value of the `args n` item, or 0 until it is known.
        private int arguments;

        /// <summary>Returns the signature the syntax declares.</summary>
        public Signature Read()
        {
            var defaults = forMacro ? Unchanged.Entry : ProcessorState.Default;
            StateListSyntax? entryList;
            (entryList, exitList) = syntax switch
            {
                ProcSignatureSyntax proc => (proc.Entry, proc.Exit),
                ImportSignatureSyntax import => (import.Entry, import.Exit),
                SignatureDeclarationSyntax set => (set.Items, null),
                _ => ((StateListSyntax?)null, (StateListSyntax?)null),
            };

            Take(entry, entryList, isExit: false);
            Take(exit, exitList, isExit: true);

            if (entry.Arguments is { Expression: { } count } args && valueOf is not null && Here(args))
            {
                if (valueOf(count) is not { } bytes)
                    report(count.Span, Catalogue.ArgsNotConstant.Message(args.Text));
                else if (bytes < 0 || bytes > 0xffff)
                    report(count.Span, Catalogue.ArgsOutOfRange.Message(args.Text));
                else
                    arguments = (int)bytes;
            }

            if (entry.Interrupt is not null)
                return Interrupt();

            var entryState = new ProcessorState(
                entry.A?.Width ?? defaults.A, entry.Index?.Width ?? defaults.Index, entry.E?.Mode ?? defaults.E,
                ValueOf(entry.D, 0xffff) ?? defaults.D, Entered(ValueOf(entry.B, 0xff)) ?? defaults.B);
            CheckEmulation(entry, entryState);
            entryState = Pinned(entryState);

            // A routine that never returns has no exit state to declare.
            if (entry.NoReturn is not null)
            {
                if (exitList is not null)
                    report(exitList.Span, Catalogue.NoreturnDeclaresAnExit);
                return Made(entryState, entryState) with { NeverReturns = true };
            }

            // An exit that names a 16-bit width but not the mode is in native mode, the only mode in
            // which that width can hold, whatever the entry's mode.
            var exitMode = exit.E?.Mode
                ?? (entryState.E == ProcessorMode.Emulation && (exit.A?.Width == Width.Sixteen || exit.Index?.Width == Width.Sixteen)
                    ? ProcessorMode.Native
                    : entryState.E);
            var exitState = new ProcessorState(
                exit.A?.Width ?? entryState.A, exit.Index?.Width ?? entryState.Index, exitMode,
                ValueOf(exit.D, 0xffff) ?? entryState.D, Handed(ValueOf(exit.B, 0xff), entryState) ?? entryState.B);

            // An impossible exit item is reported once and read as the entry's value, so that code
            // using the signature does not report the same mistake again.
            if (!Kept(exit.A, entryState.A == Width.Unchanged))
                exitState = exitState with { A = entryState.A };
            if (!Kept(exit.Index, entryState.Index == Width.Unchanged))
                exitState = exitState with { Index = entryState.Index };
            if (!Kept(exit.E, entryState.E == ProcessorMode.Unchanged))
                exitState = exitState with { E = entryState.E };
            if (!Kept(exit.D, entryState.D.Kind == StateValueKind.Unchanged))
                exitState = exitState with { D = entryState.D };
            if (!Kept(exit.B, entryState.B.IsEntered))
                exitState = exitState with { B = entryState.B };
            CheckEmulation(exit, exitState);
            return Made(entryState, Pinned(exitState));
        }

        // The registers the list promises. A list that contains its own `keeps` states which
        // they are. A list that contains none takes what the signature set it names gives.
        private static Processor.Registers Promised(Parts parts)
        {
            var own = parts.Keeps.Where(kept => !kept.FromSet).ToList();
            return (own.Count > 0 ? own : parts.Keeps)
                .Aggregate(Processor.Registers.None, (all, kept) => all | kept.Item.Registers);
        }

        // A set of banks at entry means the routine is entered with one of those banks, and
        // hands that same bank back unless the exit says otherwise.
        private static StateValue? Entered(StateValue? value) =>
            value is { Kind: StateValueKind.Among } among ? StateValue.Within(among.Banks) : value;

        // `dbr*` after the arrow, for a routine entered with one of a set of banks, means it
        // hands back whichever bank it was entered with.
        private static StateValue? Handed(StateValue? value, ProcessorState entryState) =>
            value is { Kind: StateValueKind.Unchanged } && entryState.B.Kind == StateValueKind.Within ? entryState.B : value;

        // Emulation mode pins both widths at 8 bits, so `emu` implies that too, as it does in
        // `.state`.
        private static ProcessorState Pinned(ProcessorState state) =>
            state.E == ProcessorMode.Emulation ? state with { A = Width.Eight, Index = Width.Eight } : state;

        private Signature Made(ProcessorState entered, ProcessorState exited) =>
            new(entered, exited, entry.Far?.IsFar ?? false, entry.Inline)
            {
                Arguments = arguments,
                Keeps = Promised(entry),
                syntax = syntax,
                forMacro = forMacro,
            };

        // An interrupt handler is entered from anywhere, so it may only declare which mode the
        // processor is in. It leaves by `rti`, so it declares nothing after `->`.
        private Signature Interrupt()
        {
            foreach (var other in new[] { entry.A, entry.Index, entry.D, entry.B })
            {
                if (other is { } given)
                {
                    report(At(entry, given), Catalogue.HandlerAssumesState.Message(given.Text));
                }
            }
            foreach (var other in new[] { entry.Far, entry.Inline, entry.Arguments })
            {
                if (other is { } given)
                {
                    report(At(entry, given), Catalogue.HandlerDistance.Message(given.Text));
                }
            }
            if (entry.NoReturn is { } never)
            {
                report(At(entry, never), Catalogue.HandlerNoreturn.Message(never.Text));
            }
            if (entry.E is { IsUnchanged: true } kept)
                report(At(entry, kept), Catalogue.HandlerKeeps.Message(kept.Text));
            if (exitList is not null)
                report(exitList.Span, Catalogue.HandlerDeclaresAnExit);

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

        // Whether an item appears in the signature itself rather than coming from a set it names.
        private bool Here(StateItem item) => !fromSets.Contains((item.Node.Tree, item.Node.Position));

        // Where a mistake about an item is reported: at the item, or at the set that gave it.
        private TextSpan At(Parts parts, StateItem item) =>
            Here(item) || parts.SetReference is not { } reference ? item.Node.Span : reference.Span;

        // `dp = e` has the value of e once the constants are known, and is unknown before that.
        // `dp?` is unknown and `dp*` is unchanged. A problem with a value a set gives is reported
        // where the set is declared.
        private StateValue? ValueOf(StateItem? item, long largest)
        {
            if (item is not { } given)
                return null;
            if (given.IsUnchanged)
                return StateValue.Unchanged;
            if (given.IsBankSet)
                return BanksOf(given, largest);
            if (given.Expression is not { } expression || valueOf is null)
                return StateValue.Unknown;
            if (valueOf(expression) is not { } value)
            {
                if (Here(given))
                    report(expression.Span, Catalogue.SignatureValueNotConstant.Message(given.Text));
                return StateValue.Unknown;
            }
            if (value < 0 || value > largest)
            {
                if (Here(given))
                {
                    report(expression.Span, Catalogue.SignatureValueOutOfRange.Message(
                        given.Text,
                        largest == 0xff ? "a bank is one byte" : "the direct page is a 16-bit address"));
                }
                return StateValue.Unknown;
            }
            return StateValue.Of(value);
        }

        // `dbr = [...]` means one of the banks it names. D is a single direct page and has no set
        // form.
        private StateValue BanksOf(StateItem given, long largest)
        {
            if (largest != 0xff)
            {
                if (Here(given))
                    report(given.Node.Span, Catalogue.StateBanksNotDbr.Message(given.Text));
                return StateValue.Unknown;
            }
            if (valueOf is null)
                return StateValue.Unknown;
            if (given.BanksOf(valueOf, out var invalid) is { } banks)
                return StateValue.Among(banks);
            if (Here(given))
                report(invalid!.Span, Catalogue.StateBanksInvalid.Message(given.Text));
            return StateValue.Unknown;
        }

        // Collects the items of one list by part into `parts`. A signature set comes first, and an
        // item the list contains after it replaces what the set gives for the same part.
        private void Take(Parts parts, StateListSyntax? list, bool isExit)
        {
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
                    report(item.Node.Span, Catalogue.SignatureSetNotFirst.Message(item.Text));
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
        }

        // Returns the items a set gives, with the sets it names expanded in place. Before the
        // program's names are resolved, a set gives nothing.
        private List<StateItem> Expand(StateItem reference, HashSet<Symbol> seen)
        {
            var items = new List<StateItem>();
            if (setOf?.Invoke(reference.SetName!) is not { } set)
                return items;
            if (set.Kind != SymbolKind.SignatureSet)
            {
                if (Here(reference))
                {
                    report(reference.Node.Span, Catalogue.SignatureSetNotASet.Message(reference.Text, set.KindPhrase));
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

        private void Assign(Parts parts, StateItem item, bool isExit, bool fromSet)
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
                    report(item.Node.Span, Catalogue.MacroKeeps.Message(item.Text));
                    break;
                case StatePart.Keeps when isExit:
                    report(item.Node.Span,
                        Catalogue.ItemBelongsAtEntry.Message(item.Text, "is about a routine from entry to exit"));
                    break;
                case StatePart.Keeps:
                    if (item.Registers == Processor.Registers.None)
                        break;
                    parts.Keeps.Add((item, fromSet));
                    break;

                case StatePart.NoReturn when forMacro:
                    report(item.Node.Span, Catalogue.MacroNoreturn);
                    break;
                case StatePart.Distance or StatePart.Inline or StatePart.Arguments or StatePart.Interrupt when forMacro:
                    report(item.Node.Span, Catalogue.MacroDistance.Message(item.Text));
                    break;
                case StatePart.Distance or StatePart.Inline or StatePart.Arguments or StatePart.Interrupt
                    or StatePart.NoReturn when isExit:
                    report(item.Node.Span,
                        Catalogue.ItemBelongsAtEntry.Message(item.Text, "describes how a routine is called, entered or left"));
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
                    if (!fromSet && parts.Given.Contains(StatePart.Distance)
                        && parts.Far is { } earlierDistance && earlierDistance.IsFar != item.IsFar)
                    {
                        report(item.Node.Span,
                            Catalogue.DistanceDisagrees.Message(item.Text, (earlierDistance.IsFar ? "far" : "near")));
                    }
                    if (!fromSet)
                        parts.Given.Add(StatePart.Distance);
                    parts.Far = item;
                    break;
                default:
                    break;
            }
        }

        // Two items in one list for the same part are a mistake. An item after a set replaces
        // what the set gives.
        private StateItem? Once(Parts parts, StateItem? earlier, StateItem item, bool fromSet)
        {
            if (fromSet)
                return item;
            if (earlier is { } first && !parts.Given.Add(item.Part))
                report(item.Node.Span, Catalogue.SignatureItemTwice.Message(first.Text, item.Text));
            parts.Given.Add(item.Part);
            return item;
        }

        // A routine can hand back unchanged only what it assumed nothing about.
        private bool Kept(StateItem? exitItem, bool keptAtEntry)
        {
            if (exitItem is not { IsUnchanged: true } kept || keptAtEntry)
                return true;
            report(At(exit, kept), Catalogue.UnchangedNeedsEntry.Message(kept.Text, kept.Text));
            return false;
        }

        private void CheckEmulation(Parts parts, ProcessorState state)
        {
            if (state.E != ProcessorMode.Emulation)
                return;
            foreach (var wide in new[] { parts.A, parts.Index })
            {
                if (wide is { Width: Width.Sixteen } item)
                    report(At(parts, item), Catalogue.WidthInEmulation.Message($"`{item.Text}`"));
            }
        }
    }
}
