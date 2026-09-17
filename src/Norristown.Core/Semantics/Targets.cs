using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// What a name written as a target stands for: the operand of a branch or a jump, and the
/// names an annotation lists.
/// </summary>
public static class Targets
{
    /// <summary>
    /// The label a name stands for, and the writing it belongs to, or null when the name is
    /// no label at all. A macro parameter stands for what its call gave it, and what a call
    /// gave was written in the caller, so it belongs to the caller's level rather than to
    /// the body's. A routine's address in a mirror bank stands for the routine.
    /// </summary>
    public static (Symbol Symbol, Expansion? At)? Of(SemanticModel model, SyntaxNode? expression, Expansion? on)
    {
        if (MirrorOf(model, expression, on) is { } mirror)
            return (mirror.Routine, mirror.At);
        if (expression is not { Kind: SyntaxKind.NameExpression } || model.SymbolOf(expression, on) is not { } symbol)
            return null;
        if (symbol.Kind != SymbolKind.MacroParameter)
            return (symbol, Expansion.Owning(on, symbol));
        return model.GivenAt(symbol, on) is { Argument.Value: { } given, Caller: var caller }
            ? Of(model, given, caller)
            : null;
    }

    /// <summary>
    /// A routine's address in a bank of its choosing, <c>(bank &lt;&lt; 16) | .loword(f)</c>, which
    /// is how a long jump or call reaches a routine in one of its segment's mirrors: the routine,
    /// the writing it belongs to, and the bank. Null for any other expression.
    /// </summary>
    public static (Symbol Routine, Expansion? At, long Bank)? MirrorOf(SemanticModel model, SyntaxNode? expression, Expansion? on)
    {
        if (Inner(expression) is not { Kind: SyntaxKind.BinaryExpression, ChildNodes: [var left, var right] } combined
            || !combined.ChildTokens.Any(token => token.Kind is SyntaxKind.Bar or SyntaxKind.Plus))
        {
            return null;
        }
        foreach (var (low, high) in new[] { (left, right), (right, left) })
        {
            if (Inner(low) is { Kind: SyntaxKind.CallExpression, ChildTokens: [var function, ..], ChildNodes: [var arguments] }
                && function.Text.Equals(".loword", StringComparison.OrdinalIgnoreCase)
                && arguments.ChildNodes is [{ Kind: SyntaxKind.NameExpression } named]
                && Of(model, named, on) is { Symbol.Signature: not null } routine
                && model.ValueOf(high, on).AsNumber() is { } bank and >= 0 and <= 0xff0000 && (bank & 0xffff) == 0)
            {
                return (routine.Symbol, routine.At, bank >> 16);
            }
        }
        return null;
    }

    /// <summary>An expression with the parentheses around it taken off.</summary>
    private static SyntaxNode? Inner(SyntaxNode? expression)
    {
        while (expression is { Kind: SyntaxKind.ParenthesizedExpression })
            expression = expression.ChildNodes.FirstOrDefault();
        return expression;
    }
}
