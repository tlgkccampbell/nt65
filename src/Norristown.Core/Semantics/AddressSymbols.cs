using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// The symbols with a place in a segment that an expression is built on: labels, routines and
/// data declarations, followed through address aliases and through the operands a macro call gave.
/// What memory an operand reaches through the direct page or the data bank follows from the
/// segments these are in.
/// </summary>
public static class AddressSymbols
{
    /// <summary>The placed symbols <paramref name="expression"/> names on the writing <paramref name="on"/>.</summary>
    public static IEnumerable<Symbol> In(SemanticModel model, SyntaxNode expression, Expansion? on) =>
        Collect(model, expression, on, []).Distinct();

    private static IEnumerable<Symbol> Collect(SemanticModel model, SyntaxNode node, Expansion? on, HashSet<Symbol> followed)
    {
        var names = node.Kind == SyntaxKind.NameExpression
            ? [node]
            : node.DescendantNodes().Where(child => child.Kind == SyntaxKind.NameExpression);
        foreach (var name in names)
        {
            if (model.SymbolOf(name) is not { } symbol)
                continue;

            // A field of a data declaration is at a place in the declaration's segment.
            if (symbol.Kind == SymbolKind.Member && name.ChildTokens.Length > 0
                && model.SymbolAt(name.ChildTokens[0]) is { Kind: SymbolKind.Data } instance)
            {
                symbol = instance;
            }

            switch (symbol.Kind)
            {
                case SymbolKind.Label or SymbolKind.Proc or SymbolKind.Data:
                    yield return symbol;
                    break;
                case SymbolKind.AddressAlias when symbol.ValueExpression is { } value && followed.Add(symbol):
                    foreach (var aliased in Collect(model, value, null, followed))
                        yield return aliased;
                    break;
                case SymbolKind.MacroParameter when model.GivenAt(symbol, on) is { Argument.Value: { } given, Caller: var caller }:
                    foreach (var passed in Collect(model, given, caller, followed))
                        yield return passed;
                    break;
                default:
                    break;
            }
        }
    }
}
