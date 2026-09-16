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
    /// the body's.
    /// </summary>
    public static (Symbol Symbol, Expansion? At)? Of(SemanticModel model, SyntaxNode? expression, Expansion? on)
    {
        if (expression is not { Kind: SyntaxKind.NameExpression } || model.SymbolOf(expression) is not { } symbol)
            return null;
        if (symbol.Kind != SymbolKind.MacroParameter)
            return (symbol, Expansion.Owning(on, symbol));
        return model.GivenAt(symbol, on) is { Argument.Value: { } given, Caller: var caller }
            ? Of(model, given, caller)
            : null;
    }
}
