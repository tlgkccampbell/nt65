using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// What one parameter was given at one call. An argument is a value, not the tokens it was
/// written as: the body stands it in as a parenthesized whole, so <c>value * 2</c> with the
/// argument <c>1 + 2</c> is 6.
/// </summary>
/// <param name="Parameter">The parameter it was given to.</param>
/// <param name="Value">The expression or braced operand written, or the default when the call left it out.</param>
/// <param name="Items">What a <c>list</c> parameter was given, in order; empty for every other kind.</param>
/// <param name="Block">The block a <c>block</c> parameter was given, or null when it was left out.</param>
/// <param name="Written">Whether the call wrote it, rather than taking the parameter's default.</param>
public sealed record MacroArgument(
    MacroParameter Parameter,
    SyntaxNode? Value,
    IReadOnlyList<SyntaxNode> Items,
    BlockSyntax? Block,
    bool Written)
{
    /// <summary>The word a <c>one</c> parameter was given, or null when it is not one.</summary>
    public string? Word =>
        Parameter.Kind == ParameterKind.One && Value is NameExpressionSyntax { SimpleName: { } word }
            ? word.Text
            : null;

    /// <summary>
    /// The operand an <c>operand</c> parameter stands for, unwrapped from its braces. A plain
    /// address is written without them, and is an operand all the same.
    /// </summary>
    public SyntaxNode? Operand =>
        Value is BracedOperandSyntax braced ? braced.Operand : Value;
}
