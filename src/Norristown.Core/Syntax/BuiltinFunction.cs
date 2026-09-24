namespace Norristown.Syntax;

/// <summary>
/// Describes a built-in function, one row of <see cref="SyntaxFacts.Builtins"/>.
/// </summary>
/// <param name="Kind">The function.</param>
/// <param name="Arithmetic">
/// Whether the function is arithmetic on its arguments and asks nothing about the program. These
/// are the functions a build's condition may call, since the configuration alone can evaluate
/// them.
/// </param>
/// <param name="MacroOnly">
/// Whether only a macro body may call the function, because it asks about the arguments the macro
/// was given.
/// </param>
public sealed record BuiltinFunction(BuiltinKind Kind, bool Arithmetic = false, bool MacroOnly = false)
{
    /// <summary>Gets the function's name as it is spelled in source, such as <c>.sizeof</c>.</summary>
    public string Name => SyntaxFacts.TextOf(Kind);
}
