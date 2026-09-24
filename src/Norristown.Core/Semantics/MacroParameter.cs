using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Represents one parameter of a macro, with the name its body uses, the kinds of argument it
/// accepts and the value it gets when a call leaves it out.
/// </summary>
/// <param name="Symbol">The parameter's name, as the body sees it.</param>
/// <param name="Accepts">The kind of argument the parameter accepts.</param>
/// <param name="Default">
/// The expression the parameter gets when a call leaves it out, or null when it has no default.
/// </param>
/// <param name="Empty">Whether the parameter's default is <c>{}</c>, the empty block argument.</param>
public sealed record MacroParameter(Symbol Symbol, ArgumentKind Accepts, SyntaxNode? Default, bool Empty)
{
    /// <summary>Gets the parameter's name as it appears in the source.</summary>
    public string Name => Symbol.Name;

    /// <summary>Gets the parameter's kind.</summary>
    public ParameterKind Kind => Accepts.Kind;

    /// <summary>
    /// Gets a value indicating whether the parameter takes a trailing block rather than an
    /// argument in the parentheses.
    /// </summary>
    public bool IsBlock => Kind == ParameterKind.Block;

    /// <summary>
    /// Gets a value indicating whether a call may leave the parameter out. This is so when it has
    /// a default, or when it is a <c>list</c>, which takes the remaining arguments and accepts
    /// none.
    /// </summary>
    public bool IsOptional => Default is not null || Empty || Kind == ParameterKind.List;
}
