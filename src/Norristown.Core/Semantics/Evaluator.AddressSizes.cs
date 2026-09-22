using Norristown.Processor;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// How wide an address an expression reaches: the widest of the addresses it names, or what
/// its own value implies when it names none. This is what decides between a direct-page, an
/// absolute and a long operand, and it is asked of expressions that are never evaluated for a
/// value at all.
/// </summary>
internal sealed partial class Evaluator
{
    /// <summary>The address size of an expression, with <paramref name="segment"/> giving <c>*</c> its size.</summary>
    public static AddressSize? AddressSizeOf(
        SyntaxNode expression,
        string? segment,
        SegmentTable segments,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved,
        IReadOnlyDictionary<Symbol, Expansion.Bound>? bound = null) =>
        new Evaluator(segments, resolved, null, null, bound).SizeOf(expression, segment);

    /// <summary>The wider of two address sizes, either of which may be unknown.</summary>
    private static AddressSize? Widest(AddressSize? a, AddressSize? b) =>
        a is null ? b : b is null ? a : (AddressSize)Math.Max((int)a, (int)b);

    /// <summary>
    /// The address size of an expression: a constant's value decides, and otherwise
    /// the widest of the address symbols it names.
    /// </summary>
    /// <param name="expression">The expression to size.</param>
    /// <param name="segment">The segment <c>*</c> stands in.</param>
    /// <param name="known">
    /// The expression's value where the caller has it, so that sizing an expression does not
    /// evaluate it a second time and report what it found twice.
    /// </param>
    private AddressSize? SizeOf(SyntaxNode expression, string? segment, Value? known = null)
    {
        AddressSize? widest = null;
        var named = false;
        Walk(expression);
        return named ? widest : (known ?? Evaluate(expression)).ImpliedAddressSize();

        void Walk(SyntaxNode node)
        {
            // A span is the difference of two addresses, which is a number; an end is an
            // address, as wide as the label it follows.
            if (node is CallExpressionSyntax { Function: { } function }
                && function.Text.Equals(".spanof", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (node is CurrentAddressExpressionSyntax)
            {
                named = true;
                widest = Widest(widest, SegmentSize(segment));
                return;
            }
            if (SelectArguments(node) is not null)
            {
                if (ChosenBy(node) is { } chosen)
                    Walk(chosen);
                return;
            }
            if (node is NameExpressionSyntax name)
            {
                // A member reached through an instance, `pos::y`, is a place in the instance,
                // and as wide an address as the instance is.
                if ((SymbolOf(name) is { IsAddress: true } symbol ? symbol : AddressAlong(name)) is { } address)
                {
                    named = true;
                    widest = Widest(widest, address.AddressSizeIn(name.Tree));
                }
                return;
            }
            foreach (var child in node.ChildNodes)
                Walk(child);
        }
    }

    /// <summary>Whether an expression names an address, which is what makes it an alias rather than a constant.</summary>
    private bool NamesAnAddress(SyntaxNode node)
    {
        if (node is CurrentAddressExpressionSyntax)
            return true;
        if (node is NameExpressionSyntax name)
            return SymbolOf(name) is { IsAddress: true };
        if (SelectArguments(node) is not null)
            return ChosenBy(node) is { } chosen && NamesAnAddress(chosen);
        foreach (var child in node.ChildNodes)
        {
            if (NamesAnAddress(child))
                return true;
        }
        return false;
    }

    /// <summary>The address a path of members starts from, such as the instance of <c>pos::y</c>, or null.</summary>
    private Symbol? AddressAlong(NameExpressionSyntax name)
    {
        foreach (var token in name.Names)
        {
            if (resolved.TryGetValue((name.Tree, token.Span.Start), out var part) && part.IsAddress)
            {
                Settle(part);
                return part;
            }
        }
        return null;
    }

    private AddressSize? SegmentSize(string? segment) =>
        segment is null ? null : segments.Find(segment)?.Size;
}
