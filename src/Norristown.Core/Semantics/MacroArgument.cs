using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Represents the argument one parameter received at one call. An argument is a value, not the
/// tokens it was written as. The body substitutes it as a parenthesized whole, so
/// <c>value * 2</c> with the argument <c>1 + 2</c> is 6.
/// </summary>
/// <param name="Parameter">The parameter the argument was given to.</param>
/// <param name="Value">
/// The expression or braced operand in the call, or the parameter's default when the call left it
/// out.
/// </param>
/// <param name="Items">The items a <c>list</c> parameter was given, in order; empty for every other kind.</param>
/// <param name="Block">The block a <c>block</c> parameter was given, or null when it was left out.</param>
/// <param name="IsGiven">
/// Whether the call supplied the argument, as opposed to taking the parameter's default.
/// </param>
public sealed record MacroArgument(
    MacroParameter Parameter,
    SyntaxNode? Value,
    IReadOnlyList<SyntaxNode> Items,
    BlockSyntax? Block,
    bool IsGiven)
{
    /// <summary>
    /// Gets the word a <c>one</c> parameter was given, or null when the parameter is of another
    /// kind or was not given a word.
    /// </summary>
    public string? Word =>
        Parameter.Kind == ParameterKind.One && Value is NameExpressionSyntax { SimpleName: { } word }
            ? word.Text
            : null;

    /// <summary>
    /// Gets the operand an <c>operand</c> parameter was given, unwrapped from its braces. A plain
    /// address is written without braces and is still an operand.
    /// </summary>
    public SyntaxNode? Operand =>
        Value is BracedOperandSyntax braced ? braced.Operand : Value;
}
