using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Supports <c>.endof</c> and <c>.spanof</c>, which ask where a routine or a data declaration
/// ends. They describe layout rather than shape, so their values are determined by the code that
/// has laid out the file. In the output they become a label just past the last byte and a
/// difference from that label.
/// </summary>
public static class Extents
{
    /// <summary>
    /// Determines whether a call is <c>.endof</c> or <c>.spanof</c>, setting
    /// <paramref name="span"/> to true for <c>.spanof</c>, which gives the difference rather than
    /// the address. A <c>.sizeof</c> of a routine is also a span, because the number of bytes a
    /// routine takes is a matter of layout, not shape. In a macro body, what the call measures is
    /// what the expansion <paramref name="on"/> gave its parameter.
    /// </summary>
    public static bool Is(CallExpressionSyntax call, SemanticModel model, out bool span, Expansion? on = null)
    {
        var kind = call.BuiltinKind;
        span = kind == BuiltinKind.Spanof
            || (kind == BuiltinKind.Sizeof
                && MeasuredBy(call) is { } named && model.SymbolOf(named, on) is { Kind: SymbolKind.Proc });
        return span || kind == BuiltinKind.Endof;
    }

    /// <summary>Returns the name that such a call measures, or null when it names nothing.</summary>
    public static NameExpressionSyntax? MeasuredBy(CallExpressionSyntax call) =>
        call.Arguments.Arguments.OfType<NameExpressionSyntax>().FirstOrDefault();

    /// <summary>
    /// Returns every symbol the file measures, including what the macros it calls measure with
    /// the arguments it gives them. These are the symbols whose end the output has to name, and
    /// the only ones for which layout needs to work out a span.
    /// </summary>
    public static IReadOnlySet<Symbol> MeasuredIn(SemanticModel model)
    {
        var measured = new HashSet<Symbol>();
        Visit(model.Tree.Root, null);
        return measured;

        // A macro's body measures what each call gives it, so the body of every macro the file
        // calls is visited again with that call's arguments. A parameter or a binding names
        // nothing until an expansion gives it something, so neither is measured itself.
        void Visit(SyntaxNode root, Expansion? on)
        {
            foreach (var node in root.DescendantNodes())
            {
                if (node is CallExpressionSyntax call && Is(call, model, out _, on) && MeasuredBy(call) is { } named
                    && model.SymbolOf(named, on) is { Kind: not (SymbolKind.MacroParameter or SymbolKind.Binding) } symbol)
                {
                    measured.Add(symbol);
                }
                else if (node is MacroCallSyntax macro && model.MacroAt(macro) is { Definition: BlockSyntax definition }
                    && !Expansion.Expanding(on, definition))
                {
                    Visit(definition, Expansion.Of(on, macro, definition));
                }
            }
        }
    }
}
