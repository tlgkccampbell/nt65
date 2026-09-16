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
/// The <c>inline n</c> or <c>inline .asciiz</c> item, for a routine that returns past data
/// written after each call; null for every other.
/// </param>
public sealed record Signature(ProcessorState Entry, ProcessorState Exit, bool IsFar, StateItem? Inline = null)
{
    // What the signature was read from, and whether as a macro's, so the values of its
    // `dp = e` and `dbr = e` items can be read again once the program's constants are known.
    private SyntaxNode? syntax;
    private bool forMacro;

    /// <summary>What a routine that writes no signature declares: <c>a8, i8, native, near</c>.</summary>
    public static Signature Default { get; } = new(ProcessorState.Default, ProcessorState.Default, false);

    /// <summary>What a macro that writes no signature declares: that it assumes and changes nothing.</summary>
    public static Signature Unchanged { get; } = new(
        new ProcessorState(Width.Unchanged, Width.Unchanged, ProcessorMode.Unchanged),
        new ProcessorState(Width.Unchanged, Width.Unchanged, ProcessorMode.Unchanged),
        false);

    /// <summary>How the routine is called and left, as the item that says so.</summary>
    public string Distance => IsFar ? "far" : "near";

    /// <summary>The signature as it would be written in full.</summary>
    public override string ToString() =>
        $"{Entry}, {Distance}" + (Exit == Entry ? "" : $" -> {Exit}");

    /// <summary>
    /// Reads the signature a proc, an extern proc or an import writes, reporting what is
    /// wrong with it. <paramref name="syntax"/> is the <c>: entry -&gt; exit</c> of a proc or
    /// the <c>proc(...)</c> of an import, or null where nothing was written.
    /// </summary>
    public static Signature Read(SyntaxNode? syntax, Action<TextSpan, string> report) =>
        Read(syntax, report, forMacro: false);

    /// <summary>
    /// Reads the signature a macro writes. A macro's items default to <c>*</c>, because a
    /// macro assumes and changes nothing it does not declare, and <c>near</c>, <c>far</c>
    /// and <c>inline</c> describe how a routine is called, which a macro is not.
    /// </summary>
    public static Signature ReadMacro(SyntaxNode? syntax, Action<TextSpan, string> report) =>
        Read(syntax, report, forMacro: true);

    /// <summary>
    /// The signature with the values of its <c>dp = e</c> and <c>dbr = e</c> items, which
    /// are expressions and so can be worked out only once the program's constants are.
    /// Until then those parts read as unknown. What is wrong with a value is reported to
    /// <paramref name="report"/>; everything else was reported when the signature was first read.
    /// </summary>
    public Signature Valued(Func<SyntaxNode, long?> valueOf, Action<TextSpan, string> report) =>
        syntax is null ? this : Read(syntax, (_, _) => { }, forMacro, valueOf, report);

    private static Signature Read(
        SyntaxNode? syntax, Action<TextSpan, string> report, bool forMacro,
        Func<SyntaxNode, long?>? valueOf = null, Action<TextSpan, string>? reportValue = null)
    {
        var defaults = forMacro ? Unchanged.Entry : ProcessorState.Default;
        if (syntax is null)
            return forMacro ? Unchanged : Default;

        var arrow = syntax.ChildTokens.FirstOrDefault(token => token.Kind == SyntaxKind.Arrow);
        var lists = syntax.ChildNodes.Where(node => node.Kind == SyntaxKind.StateList).ToList();
        var entryList = lists.FirstOrDefault(list => arrow.Parent is null || list.Position < arrow.Position);
        var exitList = arrow.Parent is null ? null : lists.FirstOrDefault(list => list.Position > arrow.Position);

        bool? far = null;
        StateItem? inline = null;
        var entry = Take(entryList);
        var exit = Take(exitList);

        var entryState = new ProcessorState(
            entry.A?.Width ?? defaults.A, entry.Index?.Width ?? defaults.Index, entry.E?.Mode ?? defaults.E,
            ValueOf(entry.D, 0xffff) ?? defaults.D, ValueOf(entry.B, 0xff) ?? defaults.B);
        var exitState = new ProcessorState(
            exit.A?.Width ?? entryState.A, exit.Index?.Width ?? entryState.Index, exit.E?.Mode ?? entryState.E,
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
        CheckEmulation(entry, entryState);
        CheckEmulation(exit, exitState);
        return new Signature(entryState, exitState, far ?? false, inline) { syntax = syntax, forMacro = forMacro };

        // `dp = e` is worth e once the constants are known, and unknown before; `dp?` is
        // unknown and `dp*` unchanged.
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
                reportValue?.Invoke(expression.Span, $"`{given.Text}` needs a constant: the analysis follows D and B by value");
                return StateValue.Unknown;
            }
            if (value < 0 || value > largest)
            {
                reportValue?.Invoke(expression.Span, $"`{given.Text}` is out of range: "
                    + (largest == 0xff ? "a bank is one byte" : "the direct page is a 16-bit address"));
                return StateValue.Unknown;
            }
            return StateValue.Of(value);
        }

        Parts Take(SyntaxNode? list)
        {
            var parts = default(Parts);
            foreach (var item in StateItem.Read(list))
            {
                switch (item.Part)
                {
                    case StatePart.A:
                        parts.A = Once(parts.A, item);
                        break;
                    case StatePart.Index:
                        parts.Index = Once(parts.Index, item);
                        break;
                    case StatePart.E:
                        parts.E = Once(parts.E, item);
                        break;
                    case StatePart.Distance when forMacro:
                    case StatePart.Inline when forMacro:
                        report(item.Node.Span, $"`{item.Text}` describes how a routine is called, and a macro is expanded");
                        break;
                    case StatePart.Inline:
                        if (list != entryList)
                            report(item.Node.Span, $"`{item.Text}` describes how a routine is called, and belongs before `->`");
                        else
                            inline = Once(inline, item);
                        break;
                    case StatePart.Distance:
                        if (far is { } said && said != item.IsFar)
                        {
                            report(item.Node.Span,
                                $"`{item.Text}` disagrees with `{(said ? "far" : "near")}`: a routine is called one way");
                        }
                        far = item.IsFar;
                        break;

                    case StatePart.DirectPage:
                        parts.D = Once(parts.D, item);
                        break;
                    case StatePart.DataBank:
                        parts.B = Once(parts.B, item);
                        break;
                    default:
                        break;
                }
            }
            return parts;
        }

        StateItem? Once(StateItem? earlier, StateItem item)
        {
            if (earlier is { } first)
                report(item.Node.Span, $"`{first.Text}` and `{item.Text}` both describe the same part of the state");
            return item;
        }

        // A routine can hand back unchanged only what it assumed nothing about.
        bool Kept(StateItem? exitItem, bool keptAtEntry)
        {
            if (exitItem is not { IsUnchanged: true } kept || keptAtEntry)
                return true;
            report(kept.Node.Span, $"`{kept.Text}` after `->` needs `{kept.Text}` at entry too: a routine "
                + "hands back unchanged only what it assumed nothing about");
            return false;
        }

        void CheckEmulation(Parts parts, ProcessorState state)
        {
            if (state.E != ProcessorMode.Emulation)
                return;
            foreach (var wide in new[] { parts.A, parts.Index })
            {
                if (wide is { Width: Width.Sixteen } item)
                    report(item.Node.Span, $"`{item.Text}` cannot hold in emulation mode, where both widths are 8 bits");
            }
        }
    }

    /// <summary>The items one list gives, by part.</summary>
    private struct Parts
    {
        public StateItem? A;
        public StateItem? Index;
        public StateItem? E;
        public StateItem? D;
        public StateItem? B;
    }
}
