using Norristown.Processor;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Computes how wide an address an expression reaches. This is the widest of the addresses it
/// names, or the width its own value implies when it names none. The width decides between a
/// direct-page, an absolute and a long operand, and it is requested for expressions that are
/// never evaluated for a value at all.
/// </summary>
internal sealed partial class Evaluator
{
    /// <summary>
    /// Returns the address size of an expression, with <paramref name="segment"/> giving
    /// <c>*</c> its size.
    /// </summary>
    public static AddressSize? AddressSizeOf(
        SyntaxNode expression,
        string? segment,
        SegmentTable segments,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved,
        IReadOnlyDictionary<Symbol, Expansion.Bound>? bound = null) =>
        Querying(new EvaluationInputs(segments, new BoundNames(resolved, bound))).SizeOf(expression, segment);

    /// <summary>Returns the wider of two address sizes, either of which may be unknown.</summary>
    private static AddressSize? Widest(AddressSize? a, AddressSize? b) =>
        a is null ? b : b is null ? a : (AddressSize)Math.Max((int)a, (int)b);

    /// <summary>
    /// Returns the address size of an expression. When the expression has a numeric value, that
    /// value decides; this includes a constant that is the distance between two locations in one
    /// data declaration. Otherwise the widest of the address symbols it names decides.
    /// </summary>
    /// <param name="expression">The expression to size.</param>
    /// <param name="segment">The segment that <c>*</c> is in.</param>
    /// <param name="known">
    /// The expression's value when the caller has it, so that sizing an expression does not
    /// evaluate it a second time and report its problems twice.
    /// </param>
    private AddressSize? SizeOf(SyntaxNode expression, string? segment, Value? known = null)
    {
        AddressSize? widest = null;
        var named = false;
        Walk(expression);
        if (!named)
            return (known ?? Evaluate(expression)).ImpliedAddressSize();

        // An expression that names addresses and still has a numeric value is a distance
        // between two locations in one data declaration, which is a constant, so its value
        // decides.
        return (known ?? Evaluate(expression)) is { IsNumber: true } value ? value.ImpliedAddressSize() : widest;

        void Walk(SyntaxNode node)
        {
            // A segment function's value comes from the linker, and its width is the one the
            // segment functions give.
            if (node is CallExpressionSyntax asked
                && SegmentFunctions.Of(asked, segments, name => SymbolOf(name) is not null) is not null)
            {
                named = true;
                widest = Widest(widest, SegmentFunctions.SizeOf());
                return;
            }

            // `.spanof` is the difference of two addresses, which is a number, so it adds no
            // width. `.endof` needs no case, because it is an address as wide as the symbol it
            // measures, which walking its argument finds.
            if (node is CallExpressionSyntax { BuiltinKind: BuiltinKind.Spanof })
            {
                return;
            }
            // `.exprof(p)` is as wide as the prefix the operand was given with, as in
            // `{a:ptr}`, or otherwise as wide as the expression it stands for.
            if (node is CallExpressionSyntax exprOf && Operands.IsExprOf(exprOf))
            {
                if (names.OperandOf(exprOf) is { } operand && Operands.PrefixSize(operand) is { } prefixSize)
                {
                    named = true;
                    widest = Widest(widest, prefixSize);
                }
                else if (names.ExprOf(exprOf) is { } inner)
                {
                    Walk(inner);
                }
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
                // A member reached through an instance, such as `pos::y`, is a location in the
                // instance, and as wide an address as the instance is.
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

    /// <summary>
    /// Determines whether an expression names an address, which makes it an alias rather than a
    /// constant.
    /// </summary>
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

    /// <summary>
    /// Returns the address a path of members starts from, such as the instance in
    /// <c>pos::y</c>, or null.
    /// </summary>
    private Symbol? AddressAlong(NameExpressionSyntax name)
    {
        foreach (var token in name.Names)
        {
            if (resolved.TryGetValue((name.Tree, token.Span.Start), out var part) && part.IsAddress)
            {
                EnsureEvaluated(part);
                return part;
            }
        }
        return null;
    }

    private AddressSize? SegmentSize(string? segment) =>
        segment is null ? null : segments.Find(segment)?.Size;
}
