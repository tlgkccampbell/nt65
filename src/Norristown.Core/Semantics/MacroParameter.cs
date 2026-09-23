using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// One parameter of a macro: the name its body uses, what an argument of it may be, and
/// what it gets when a call leaves it out.
/// </summary>
/// <param name="Symbol">The name, as the body sees it.</param>
/// <param name="Accepts">What an argument of it may be.</param>
/// <param name="Default">The expression a call that leaves it out gets, or null when it has none.</param>
/// <param name="Empty">Whether its default is <c>{}</c>, the block argument with nothing in it.</param>
public sealed record MacroParameter(Symbol Symbol, ArgumentKind Accepts, SyntaxNode? Default, bool Empty)
{
    /// <summary>The name as the source writes it.</summary>
    public string Name => Symbol.Name;

    /// <summary>Which kind it is.</summary>
    public ParameterKind Kind => Accepts.Kind;

    /// <summary>Whether it takes a trailing block rather than an argument in the parentheses.</summary>
    public bool IsBlock => Kind == ParameterKind.Block;

    /// <summary>
    /// Whether a call may leave it out: it has a default, or it is a <c>list</c>, which
    /// takes the remaining arguments and is content with none.
    /// </summary>
    public bool IsOptional => Default is not null || Empty || Kind == ParameterKind.List;
}
