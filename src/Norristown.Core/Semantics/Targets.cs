using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Resolves what a name used as a target refers to. Targets are the operand of a branch or a
/// jump, and the names an annotation lists.
/// </summary>
public static class Targets
{
    /// <summary>
    /// Returns the symbol a target names and the <see cref="Expansion"/> it belongs to, or null
    /// when it names no symbol. A macro parameter is followed to the argument its call gave it.
    /// That argument is part of the caller, so it belongs to the caller's level rather than to
    /// the body's. A routine's address in a mirror bank is treated as the routine.
    /// </summary>
    public static (Symbol Symbol, Expansion? At)? Of(SemanticModel model, SyntaxNode? expression, Expansion? on)
    {
        if (MirrorOf(model, expression, on) is { } mirror)
            return (mirror.Routine, mirror.At);
        if (expression is not NameExpressionSyntax name || model.SymbolOf(name, on) is not { } symbol)
            return null;
        if (symbol.Kind != SymbolKind.MacroParameter)
            return (symbol, Expansion.Owning(on, symbol));
        return model.GivenAt(symbol, on) is { Argument.Value: { } given, Caller: var caller }
            ? Of(model, given, caller)
            : null;
    }

    /// <summary>
    /// Recognizes a routine's address in a chosen bank, <c>(bank &lt;&lt; 16) | .loword(f)</c>,
    /// which is how a long jump or call reaches a routine in one of its segment's mirrors. Returns
    /// the routine, the <see cref="Expansion"/> it belongs to, and the bank, or null for any other
    /// expression.
    /// </summary>
    public static (Symbol Routine, Expansion? At, long Bank)? MirrorOf(SemanticModel model, SyntaxNode? expression, Expansion? on)
    {
        if (Inner(expression) is not BinaryExpressionSyntax { OperatorToken.Kind: SyntaxKind.Bar or SyntaxKind.Plus } combined)
            return null;
        foreach (var (low, high) in new[] { (combined.Left, combined.Right), (combined.Right, combined.Left) })
        {
            if (Inner(low) is CallExpressionSyntax { BuiltinKind: BuiltinKind.Loword } call
                && call.Arguments.Arguments is [NameExpressionSyntax named]
                && Of(model, named, on) is { Symbol.Signature: not null } routine
                && model.ValueOf(high, on).AsNumber() is { } bank and >= 0 and <= 0xff0000 && (bank & 0xffff) == 0)
            {
                return (routine.Symbol, routine.At, bank >> 16);
            }
        }
        return null;
    }

    /// <summary>Returns <paramref name="expression"/> with the parentheses around it removed.</summary>
    private static SyntaxNode? Inner(SyntaxNode? expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;
        return expression;
    }
}
