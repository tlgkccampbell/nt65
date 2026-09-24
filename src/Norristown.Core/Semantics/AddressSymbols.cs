using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Finds the symbols with an address in a segment (labels, routines and data declarations) that
/// an expression refers to, by following address aliases and the operands a macro call passed.
/// The memory an operand reaches through the direct page or the data bank depends on the
/// segments these symbols are in.
/// </summary>
public static class AddressSymbols
{
    /// <summary>
    /// Returns the symbols with an address in a segment that <paramref name="expression"/> refers
    /// to, resolving macro parameters in the expansion <paramref name="on"/>.
    /// </summary>
    public static IEnumerable<Symbol> In(SemanticModel model, SyntaxNode expression, Expansion? on) =>
        Collect(model, expression, on, []).Distinct();

    private static IEnumerable<Symbol> Collect(SemanticModel model, SyntaxNode node, Expansion? on, HashSet<Symbol> followed)
    {
        var names = node is NameExpressionSyntax single
            ? [single]
            : node.DescendantNodes().OfType<NameExpressionSyntax>();
        foreach (var name in names)
        {
            if (model.SymbolOf(name) is not { } symbol)
                continue;

            // A field of a data declaration has an address in the declaration's segment.
            if (symbol.Kind == SymbolKind.Member && name is { GlobalToken: null, Names: [var outermost, ..] }
                && model.SymbolAt(outermost) is { Kind: SymbolKind.Data } instance)
            {
                symbol = instance;
            }

            switch (symbol.Kind)
            {
                case SymbolKind.Label or SymbolKind.Proc or SymbolKind.Data:
                    yield return symbol;
                    break;

                // An import that declares which segment it is in is treated as having an
                // address in that segment.
                case SymbolKind.ImportedAddress when symbol.Segment is not null:
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
