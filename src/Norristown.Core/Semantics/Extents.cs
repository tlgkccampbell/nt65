using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// What <c>.endof</c> and <c>.spanof</c> ask about: where a routine or a data declaration
/// ends. They describe layout rather than shape, so what they are worth is
/// settled by whoever has laid the file out, and in the output they become a label just
/// past the last byte and a difference from it.
/// </summary>
public static class Extents
{
    /// <summary>
    /// Whether a call is <c>.endof</c> or <c>.spanof</c>, with <paramref name="span"/> true
    /// for the second, which is the difference rather than the address. <c>.sizeof</c> of a
    /// routine is its span too: how many bytes a routine takes is layout, not shape.
    /// </summary>
    public static bool Is(SyntaxNode call, SemanticModel model, out bool span)
    {
        span = false;
        if (call.Kind != SyntaxKind.CallExpression || call.ChildTokens.Length == 0
            || call.ChildTokens[0].Kind != SyntaxKind.Directive)
        {
            return false;
        }
        var name = call.ChildTokens[0].Text;
        span = name.Equals(".spanof", StringComparison.OrdinalIgnoreCase)
            || (name.Equals(".sizeof", StringComparison.OrdinalIgnoreCase)
                && MeasuredBy(call) is { } named && model.SymbolOf(named) is { Kind: SymbolKind.Proc });
        return span || name.Equals(".endof", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The name such a call measures, or null when it names nothing.</summary>
    public static SyntaxNode? MeasuredBy(SyntaxNode call) =>
        call.ChildNodes.FirstOrDefault(child => child.Kind == SyntaxKind.ArgumentList)
            ?.ChildNodes.FirstOrDefault(argument => argument.Kind == SyntaxKind.NameExpression);

    /// <summary>
    /// Everything the file measures. These are the symbols whose end the output has to name,
    /// and the only ones layout need work a span out for.
    /// </summary>
    public static IReadOnlySet<Symbol> MeasuredIn(SemanticModel model)
    {
        var measured = new HashSet<Symbol>();
        foreach (var node in model.Tree.Root.DescendantNodes())
        {
            if (Is(node, model, out _) && MeasuredBy(node) is { } named && model.SymbolOf(named) is { } symbol)
                measured.Add(symbol);
        }
        return measured;
    }
}
