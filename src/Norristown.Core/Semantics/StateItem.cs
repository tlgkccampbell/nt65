using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// One item of a signature or a <c>.state</c>, read: which part of the state it is about and
/// what it says of it. The items share one grammar wherever they are written, so they are
/// read in one place.
/// </summary>
/// <param name="Node">The item as it is written.</param>
/// <param name="Part">Which part of the state it is about.</param>
/// <param name="Width">What it says of a width; meaningless for the other parts.</param>
/// <param name="Mode">What it says of the emulation flag; meaningless for the other parts.</param>
/// <param name="IsFar">Whether a <see cref="StatePart.Distance"/> item says <c>far</c>.</param>
/// <param name="IsUnchanged">Whether it is a <c>*</c> item, which describes a routine rather than a point in it.</param>
public readonly record struct StateItem(
    SyntaxNode Node, StatePart Part, Width Width, ProcessorMode Mode, bool IsFar, bool IsUnchanged)
{
    /// <summary>The item as it is written, for a message that names it.</summary>
    public string Text => Node.GetText().Trim();

    /// <summary>The expression after the item's name: the <c>n</c> of <c>inline n</c>, the <c>e</c> of <c>dp = e</c>.</summary>
    public SyntaxNode? Expression => Node.ChildNodes.FirstOrDefault();

    /// <summary>Whether an <c>inline</c> item says <c>inline .strz</c>.</summary>
    public bool IsStrz => Node.ChildTokens.Any(token =>
        token.Text.Equals(".strz", StringComparison.OrdinalIgnoreCase));

    /// <summary>The registers a <see cref="StatePart.Keeps"/> item names; none for every other item.</summary>
    public Layout.Registers Registers
    {
        get
        {
            var registers = Layout.Registers.None;
            foreach (var token in Node.ChildTokens.Skip(1))
            {
                if (Layout.RegisterEffects.Named(token.Text) is { } register)
                    registers |= register;
            }
            return registers;
        }
    }

    /// <summary>The items of a state list, or of a <c>.state</c>, in the order they are written.</summary>
    public static IEnumerable<StateItem> Read(SyntaxNode? list)
    {
        foreach (var node in list?.ChildNodes ?? [])
        {
            if (node.Kind == SyntaxKind.StateList)
            {
                foreach (var item in Read(node))
                    yield return item;
            }
            else if (node.Kind == SyntaxKind.StateItem && Of(node) is { } item)
            {
                yield return item;
            }
        }
    }

    /// <summary>The name of the signature set a <see cref="StatePart.Set"/> item names.</summary>
    public SyntaxNode? SetName => Part == StatePart.Set ? Node.ChildNodes.FirstOrDefault() : null;

    /// <summary>
    /// Whether a list says something and every item of it is a <c>keeps</c>. Such a list says
    /// what a register holds and nothing about the processor state, so it neither declares a
    /// label nor answers what a routine with no body assumes.
    /// </summary>
    public static bool OnlyKeeps(SyntaxNode? list)
    {
        var items = Read(list).ToList();
        return items.Count > 0 && items.TrueForAll(item => item.Part == StatePart.Keeps);
    }

    /// <summary>One item, or null when its line did not parse into one.</summary>
    private static StateItem? Of(SyntaxNode node)
    {
        if (node.ChildTokens.Length == 0)
        {
            return node.ChildNodes is [{ Kind: SyntaxKind.NameExpression }]
                ? new StateItem(node, StatePart.Set, Width.Unknown, ProcessorMode.Unknown, false, false)
                : null;
        }
        if (node.ChildTokens[0].Kind == SyntaxKind.Question)
            return new StateItem(node, StatePart.AllUnknown, Width.Unknown, ProcessorMode.Unknown, false, false);

        var name = node.ChildTokens[0].Text.ToLowerInvariant();
        var suffix = node.ChildTokens.Length > 1 ? node.ChildTokens[1].Kind : SyntaxKind.None;
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
