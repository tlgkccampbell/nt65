using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// What a macro parameter accepts, as it is written after the <c>:</c>. A <c>one</c>
/// carries the words it allows, a <c>list</c> what each of its items is, a <c>const</c> the
/// range it takes, an <c>operand</c> the modes it takes and an enum kind the enum, so each kind
/// carries whatever was written inside its parentheses.
/// </summary>
/// <param name="Kind">Which kind it is.</param>
/// <param name="Words">The words a <c>one</c> accepts, or the modes an <c>operand</c> does; empty for every other kind, and for an <c>operand</c> that takes any mode.</param>
/// <param name="Element">What each item of a <c>list</c> is; null when it is not one.</param>
public sealed record ArgumentKind(ParameterKind Kind, IReadOnlyList<string> Words, ArgumentKind? Element)
{
    /// <summary>An expression, which is what a parameter with no kind written takes.</summary>
    public static readonly ArgumentKind Expression = new(ParameterKind.Expr, [], null);

    /// <summary>The words an <c>operand(...)</c> may list: the modes <c>.mode</c> gives, plus <c>zp</c>, <c>zpx</c> and <c>zpy</c>, the direct-page forms that <c>.mode</c> reports as <c>abs</c>, <c>absx</c> and <c>absy</c>.</summary>
    public static IReadOnlyList<string> OperandModes { get; } =
        ["imm", "acc", "abs", "absx", "absy", "zp", "zpx", "zpy", "ind", "indx", "indy", "sr", "sry", "long", "longy"];

    /// <summary>The least value a <c>const(...)</c> takes, as it is written; null when it names no range.</summary>
    public ExpressionSyntax? Low { get; init; }

    /// <summary>The greatest value a <c>const(...)</c> takes, as it is written; null when it names no range.</summary>
    public ExpressionSyntax? High { get; init; }

    /// <summary>The enum whose members an enum kind takes, as it is written; null for every other kind.</summary>
    public NameExpressionSyntax? Enum { get; init; }

    /// <summary>How it is written, as a diagnostic names it.</summary>
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
    /// What is written after the <c>:</c> of a parameter, read from the syntax.
    /// A kind the parser could not read is treated as an expression, the kind that accepts
    /// the most, so that arguments are not reported against a header that is already wrong.
    /// </summary>
    public static ArgumentKind Read(ParameterKindSyntax? written)
    {
        if (written?.Type is { } type)
            return new ArgumentKind(ParameterKind.Enum, [], null) { Enum = type };

        // A `:` with no kind after it, or one the parser could not read, leaves the word missing.
        if (written?.Keyword is not { IsMissing: false } keyword)
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
            return new ArgumentKind(kind, [.. written.Words.Select(word => word.Name.Text)], null);
        if (kind == ParameterKind.Operand)
            return new ArgumentKind(kind, [.. written.Words.Select(word => word.Name.Text.ToLowerInvariant())], null);
        if (kind == ParameterKind.List)
            return new ArgumentKind(kind, [], Read(written.Element));
        if (kind == ParameterKind.Const)
            return new ArgumentKind(kind, [], null) { Low = written.Low, High = written.High };
        return new ArgumentKind(kind, [], null);
    }
}
