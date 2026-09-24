using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Represents what a macro parameter accepts, as declared after the <c>:</c>. Each kind carries
/// the contents of its parentheses. A <c>one</c> carries the words it allows, a <c>list</c> the
/// kind of each item, a <c>const</c> its range, an <c>operand</c> the modes it takes, and an
/// enum kind its enum.
/// </summary>
/// <param name="Kind">The kind of parameter.</param>
/// <param name="Words">
/// The words a <c>one</c> accepts, or the modes an <c>operand</c> accepts. Empty for every other
/// kind, and for an <c>operand</c> that takes any mode.
/// </param>
/// <param name="Element">The kind of each item of a <c>list</c>, or null for any other kind.</param>
public sealed record ArgumentKind(ParameterKind Kind, IReadOnlyList<string> Words, ArgumentKind? Element)
{
    /// <summary>The expression kind, which a parameter with no declared kind takes.</summary>
    public static readonly ArgumentKind Expression = new(ParameterKind.Expr, [], null);

    /// <summary>
    /// Gets the words an <c>operand(...)</c> may list. These are the modes <c>.mode</c> returns,
    /// plus <c>zp</c>, <c>zpx</c> and <c>zpy</c>, the direct-page forms that <c>.mode</c> reports
    /// as <c>abs</c>, <c>absx</c> and <c>absy</c>.
    /// </summary>
    public static IReadOnlyList<string> OperandModes { get; } =
        ["imm", "acc", "abs", "absx", "absy", "zp", "zpx", "zpy", "ind", "indx", "indy", "sr", "sry", "long", "longy"];

    /// <summary>
    /// Gets the least value a <c>const(...)</c> takes, as it appears in the source, or null when
    /// it names no range.
    /// </summary>
    public ExpressionSyntax? Low { get; init; }

    /// <summary>
    /// Gets the greatest value a <c>const(...)</c> takes, as it appears in the source, or null
    /// when it names no range.
    /// </summary>
    public ExpressionSyntax? High { get; init; }

    /// <summary>
    /// Gets the enum whose members an enum kind takes, as it appears in the source, or null for
    /// every other kind.
    /// </summary>
    public NameExpressionSyntax? Enum { get; init; }

    /// <summary>Returns the kind as it appears in the source, in the form a diagnostic uses.</summary>
    public override string ToString() => Kind switch
    {
        ParameterKind.One => $"one({string.Join(", ", Words)})",
        ParameterKind.List => $"list({Element?.ToString() ?? "expr"})",
        ParameterKind.Const when Low is not null && High is not null =>
            $"const({Low.GetText().Trim()}..{High.GetText().Trim()})",
        ParameterKind.Operand when Words.Count > 0 => $"operand({string.Join(", ", Words)})",
        ParameterKind.Enum => Enum?.GetText().Trim() ?? "expr",
        _ => Kind.ToString().ToLowerInvariant(),
    };

    /// <inheritdoc/>
    public bool Equals(ArgumentKind? other) =>
        other is not null && ToString() == other.ToString();

    /// <inheritdoc/>
    public override int GetHashCode() => ToString().GetHashCode(StringComparison.Ordinal);

    /// <summary>
    /// Reads the kind declared after a parameter's <c>:</c> from the syntax. A kind the parser
    /// could not read is treated as an expression, the kind that accepts the most, so that
    /// arguments are not reported against a header that is already wrong.
    /// </summary>
    public static ArgumentKind Read(ParameterKindSyntax? syntax)
    {
        if (syntax?.Type is { } type)
            return new ArgumentKind(ParameterKind.Enum, [], null) { Enum = type };

        // A `:` with no kind after it, or one the parser could not read, leaves the word missing.
        if (syntax?.Keyword is not { IsMissing: false } keyword)
            return Expression;

        var kind = keyword.Text.ToLowerInvariant() switch
        {
            "const" => ParameterKind.Const,
            "ident" => ParameterKind.Ident,
            "operand" => ParameterKind.Operand,
            "one" => ParameterKind.One,
            "list" => ParameterKind.List,
            "block" => ParameterKind.Block,
            _ => ParameterKind.Expr,
        };

        // Every name inside the parentheses is a word it accepts, or for an operand a mode.
        if (kind == ParameterKind.One)
            return new ArgumentKind(kind, [.. syntax.Words.Select(word => word.Name.Text)], null);
        if (kind == ParameterKind.Operand)
            return new ArgumentKind(kind, [.. syntax.Words.Select(word => word.Name.Text.ToLowerInvariant())], null);
        if (kind == ParameterKind.List)
            return new ArgumentKind(kind, [], Read(syntax.Element));
        if (kind == ParameterKind.Const)
            return new ArgumentKind(kind, [], null) { Low = syntax.Low, High = syntax.High };
        return new ArgumentKind(kind, [], null);
    }
}
