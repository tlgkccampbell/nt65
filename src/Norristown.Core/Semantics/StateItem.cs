using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Represents one parsed item of a signature or a <c>.state</c>: which part of the state it is
/// about and what it states about that part. The items share one grammar wherever they appear,
/// so they are read in one place.
/// </summary>
/// <param name="Node">The item's syntax.</param>
/// <param name="Part">The part of the state the item is about.</param>
/// <param name="Width">The width the item gives; meaningless for the other parts.</param>
/// <param name="Mode">The emulation-flag value the item gives; meaningless for the other parts.</param>
/// <param name="IsFar">Whether a <see cref="StatePart.Distance"/> item is <c>far</c>.</param>
/// <param name="IsUnchanged">
/// Whether the item is a <c>*</c> item, which describes a routine rather than a point in it.
/// </param>
public readonly record struct StateItem(
    StateItemSyntax Node, StatePart Part, Width Width, ProcessorMode Mode, bool IsFar, bool IsUnchanged)
{
    /// <summary>Gets the item's source text, for a message that names it.</summary>
    public string Text => Node.GetText().Trim();

    /// <summary>
    /// Gets the expression after the item's name, such as the <c>n</c> of <c>inline n</c> or the
    /// <c>e</c> of <c>dp = e</c>.
    /// </summary>
    public ExpressionSyntax? Expression => Node is StateValueItemSyntax valued ? valued.Value : null;

    /// <summary>
    /// Gets a value indicating whether the item gives its part a set of banks, as in
    /// <c>dbr = [$00..$3f, $80..$bf]</c>, rather than one value.
    /// </summary>
    public bool IsBankSet => Node is StateBanksItemSyntax;

    /// <summary>Gets a value indicating whether an <c>inline</c> item is <c>inline .strz</c>.</summary>
    public bool IsStrz => Node is StateInlineItemSyntax;

    /// <summary>
    /// Gets the registers a <see cref="StatePart.Keeps"/> item names, or none for every other item.
    /// </summary>
    public Processor.Registers Registers
    {
        get
        {
            if (Node is not StateKeepsItemSyntax keeps)
                return Processor.Registers.None;
            var registers = Processor.Registers.None;
            foreach (var register in keeps.Registers)
            {
                if (Processor.RegisterEffects.Named(register.Name.Text) is { } named)
                    registers |= named;
            }
            return registers;
        }
    }

    /// <summary>Reads the items of a state list, or of a <c>.state</c>, in source order.</summary>
    public static IEnumerable<StateItem> Read(SyntaxNode? list)
    {
        foreach (var node in list?.ChildNodes ?? [])
        {
            if (node is StateListSyntax nested)
            {
                foreach (var item in Read(nested))
                    yield return item;
            }
            else if (node is StateItemSyntax written && Of(written) is { } item)
            {
                yield return item;
            }
        }
    }

    /// <summary>Gets the name of the signature set a <see cref="StatePart.Set"/> item names.</summary>
    public NameExpressionSyntax? SetName =>
        Part == StatePart.Set && Node is StateSetItemSyntax set ? set.Name : null;

    /// <summary>
    /// Returns a value indicating whether a list has items and every item is a <c>keeps</c>. Such
    /// a list states what a register holds and nothing about the processor state. It therefore
    /// neither declares a label's state nor counts as the declaration a routine with no body
    /// needs.
    /// </summary>
    public static bool OnlyKeeps(SyntaxNode? list)
    {
        var items = Read(list).ToList();
        return items.Count > 0 && items.TrueForAll(item => item.Part == StatePart.Keeps);
    }

    /// <summary>
    /// Returns the banks a <c>dbr = [...]</c> item names, each evaluated with
    /// <paramref name="valueOf"/>. When they do not form a set, returns null and sets
    /// <paramref name="invalid"/> to the range that is not a bank or a run of banks, or to the
    /// item itself when it names no ranges.
    /// </summary>
    public BankSet? BanksOf(Func<ExpressionSyntax, long?> valueOf, out SyntaxNode? invalid)
    {
        invalid = Node;
        if (Node is not StateBanksItemSyntax written || written.Ranges.Count == 0)
            return null;
        var banks = default(BankSet);
        foreach (var range in written.Ranges)
        {
            var start = valueOf(range.First);
            var end = range.Last is { } last ? valueOf(last) : start;
            if (start is not { } first || end is not { } final || first is < 0 or > 0xff || final is < 0 or > 0xff
                || first > final)
            {
                invalid = range;
                return null;
            }
            banks = banks.With(first, final);
        }
        invalid = null;
        return banks;
    }

    /// <summary>Reads one item, or returns null when its syntax did not parse into an item.</summary>
    private static StateItem? Of(StateItemSyntax node) => node switch
    {
        StateUnknownItemSyntax =>
            new StateItem(node, StatePart.AllUnknown, Width.Unknown, ProcessorMode.Unknown, false, false),
        StateSetItemSyntax =>
            new StateItem(node, StatePart.Set, Width.Unknown, ProcessorMode.Unknown, false, false),
        StateFlagItemSyntax flag => Worded(node, flag.Name, flag.SuffixToken?.Kind ?? SyntaxKind.None),
        StateValueItemSyntax valued =>
            Worded(node, valued.Name, valued.EqualsToken is null ? SyntaxKind.None : SyntaxKind.Equals),
        StateBanksItemSyntax banks => Worded(node, banks.Name, SyntaxKind.Equals),
        StateInlineItemSyntax inline => Worded(node, inline.Name, SyntaxKind.None),
        StateKeepsItemSyntax keeps => Worded(node, keeps.Name, SyntaxKind.None),
        _ => null,
    };

    /// <summary>
    /// Returns the item that a state word and the <c>*</c>, <c>?</c> or <c>=</c> after it form, or
    /// null for a word that names no part of the state.
    /// </summary>
    private static StateItem? Worded(StateItemSyntax node, SyntaxToken word, SyntaxKind suffix)
    {
        var name = word.Text.ToLowerInvariant();
        var width = suffix switch
        {
            SyntaxKind.Star => Width.Unchanged,
            SyntaxKind.Question => Width.Unknown,
            _ => name.EndsWith("16", StringComparison.Ordinal) ? Width.Sixteen : Width.Eight,
        };
        var mode = suffix switch
        {
            SyntaxKind.Star => ProcessorMode.Unchanged,
            SyntaxKind.Question => ProcessorMode.Unknown,
            _ => name == "emu" ? ProcessorMode.Emulation : ProcessorMode.Native,
        };
        StatePart? part = name switch
        {
            "a" or "a8" or "a16" => StatePart.A,
            "i" or "i8" or "i16" => StatePart.Index,
            "e" or "native" or "emu" => StatePart.E,
            "near" or "far" => StatePart.Distance,
            "inline" => StatePart.Inline,
            "args" => StatePart.Arguments,
            "interrupt" => StatePart.Interrupt,
            "noreturn" => StatePart.NoReturn,
            "dp" => StatePart.DirectPage,
            "dbr" => StatePart.DataBank,
            "keeps" => StatePart.Keeps,
            _ => null,
        };
        return part is { } known
            ? new StateItem(node, known, width, mode, name == "far", suffix == SyntaxKind.Star)
            : null;
    }
}
