using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Identifies a location in memory that an instruction names directly, as the memory part of
/// <see cref="InputSources"/> follows it. Where data lands is known only once the program is
/// linked, so a location is named by what the source says rather than by its address. That is a
/// symbol, or a path to a member of a struct, and a constant offset from it, as in <c>ptr+1</c>.
/// <para>
/// Two spellings of one location that resolve differently are two locations here. This is a best
/// guess for showing. The one check that warns from it, <see cref="ScratchChecks"/>, looks only
/// at data that <c>.scratch</c> declares, which the programmer vouches is working storage.
/// </para>
/// </summary>
/// <param name="Root">
/// The symbol the location is in, or null for a member of a struct, which <paramref name="Path"/>
/// names instead.
/// </param>
/// <param name="Path">The name of a struct member as the source wrote it, such as <c>banks::source</c>, or empty.</param>
/// <param name="Offset">The constant added to the symbol's address.</param>
internal readonly record struct Location(Symbol? Root, string Path, long Offset)
{
    /// <summary>Gets the name the location is reported under, such as <c>ptr+1</c>.</summary>
    public string Name =>
        (Root?.DisplayName ?? Path) + (Offset > 0 ? $"+{Offset}" : Offset < 0 ? $"{Offset}" : "");

    /// <summary>
    /// Gets the root symbol that locations are grouped under. The bytes of one symbol, such as
    /// <c>ptr</c> and <c>ptr+1</c>, and the members of one struct are one group.
    /// </summary>
    public string Group => Root?.DisplayName ?? Path.Split("::")[0];

    /// <summary>
    /// Gets the location's address where the source fixes it, as an address alias does, or null
    /// where only linking decides it.
    /// </summary>
    public long? Address => Root?.Value.AsNumber() is { } at ? at + Offset : null;

    /// <summary>
    /// Returns the location <paramref name="expression"/> names in the expansion
    /// <paramref name="on"/>, or null where it names none. A macro parameter is followed to the
    /// argument its call gave it.
    /// </summary>
    public static Location? Of(SemanticModel model, SyntaxNode? expression, Expansion? on)
    {
        switch (expression)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return Of(model, parenthesized.Expression, on);

            case BinaryExpressionSyntax binary when binary.OperatorToken.Kind is SyntaxKind.Plus or SyntaxKind.Minus:
                var sign = binary.OperatorToken.Kind == SyntaxKind.Minus ? -1 : 1;
                if (Of(model, binary.Left, on) is { } left && model.ValueOf(binary.Right, on).AsNumber() is { } right)
                    return left with { Offset = left.Offset + (sign * right) };
                if (sign == 1 && model.ValueOf(binary.Left, on).AsNumber() is { } before && Of(model, binary.Right, on) is { } after)
                    return after with { Offset = after.Offset + before };
                return null;

            case NameExpressionSyntax name when model.SymbolOf(name, on) is { } symbol:
                if (symbol.Kind == SymbolKind.MacroParameter)
                {
                    return model.GivenAt(symbol, on) is { Argument.Value: { } given, Caller: var caller }
                        ? Of(model, given, caller)
                        : null;
                }
                // A hardware register holds what the hardware puts there, so it is not a location
                // whose value the program sets, and neither is a member reached through one.
                if (IsHardware(symbol)
                    || (symbol.Kind == SymbolKind.Member && name.Tree == model.Tree
                        && IsHardware(model.ReferenceAt(name.Parts[0].Span.Start)?.Symbol)))
                {
                    return null;
                }
                return symbol.Kind switch
                {
                    SymbolKind.Label or SymbolKind.AddressAlias or SymbolKind.Data or SymbolKind.ImportedAddress
                        => new Location(symbol, "", 0),
                    SymbolKind.Member => new Location(null, string.Join("::", name.Parts.Select(part => part.GetText().Trim())), 0),
                    _ => null,
                };

            default:
                return null;
        }
    }

    /// <summary>
    /// Returns whether <paramref name="symbol"/> is scratch data, which <c>.scratch</c> declares.
    /// </summary>
    public static bool IsScratch(Symbol? symbol) =>
        (symbol?.Data?.Parent ?? symbol?.ValueExpression?.Parent)
            is DataDeclarationSyntax { Keyword.DirectiveKind: DirectiveKind.Scratch };

    /// <summary>Returns whether <paramref name="symbol"/> is a hardware register, which <c>.mmio</c> declares.</summary>
    private static bool IsHardware(Symbol? symbol) =>
        symbol?.ValueExpression?.Parent is DataDeclarationSyntax { Keyword.DirectiveKind: DirectiveKind.Mmio };
}
