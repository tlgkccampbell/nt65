using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// What a macro parameter accepts, as it is written after the <c>:</c>. A <c>one</c>
/// carries the words it allows and a <c>list</c> what each of its items is, so the two
/// that take something inside them describe it here rather than by pointing back at syntax.
/// </summary>
/// <param name="Kind">Which kind it is.</param>
/// <param name="Words">The words a <c>one</c> accepts, empty for every other kind.</param>
/// <param name="Element">What each item of a <c>list</c> is; null when it is not one.</param>
public sealed record ArgumentKind(ParameterKind Kind, IReadOnlyList<string> Words, ArgumentKind? Element)
{
    /// <summary>An expression, which is what a parameter with no kind written takes.</summary>
    public static readonly ArgumentKind Expression = new(ParameterKind.Expr, [], null);

    /// <summary>How it is written, as a diagnostic names it.</summary>
    public override string ToString() => Kind switch
    {
        ParameterKind.One => $"one({string.Join(", ", Words)})",
        ParameterKind.List => $"list({Element?.ToString() ?? "expr"})",
        _ => Kind.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// What is written after the <c>:</c> of a parameter, read from the syntax.
    /// A kind the parser could not read is an expression, which is the kind that
    /// accepts most and so says least about an argument that is already wrong.
    /// </summary>
    public static ArgumentKind Read(SyntaxNode? node)
    {
        if (node is not { Kind: SyntaxKind.ParameterKind } || node.ChildTokens.Length == 0)
            return Expression;

        var kind = node.ChildTokens[0].Text.ToLowerInvariant() switch
        {
            "const" => ParameterKind.Const,
            "ident" => ParameterKind.Ident,
            "operand" => ParameterKind.Operand,
            "one" => ParameterKind.One,
            "list" => ParameterKind.List,
            "block" => ParameterKind.Block,
            _ => ParameterKind.Expr,
        };

        if (kind == ParameterKind.One)
        {
            // Every name inside the parentheses is a word it accepts; the `one` itself is the
            // first token, and the punctuation carries nothing.
            return new ArgumentKind(kind, [.. node.ChildTokens.Skip(1)
                .Where(token => token.Kind is SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic)
                .Select(token => token.Text)], null);
        }
        if (kind == ParameterKind.List)
            return new ArgumentKind(kind, [], Read(node.ChildNodes.FirstOrDefault()));
        return new ArgumentKind(kind, [], null);
    }
}
