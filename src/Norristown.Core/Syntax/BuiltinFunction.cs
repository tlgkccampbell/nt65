namespace Norristown.Syntax;

/// <summary>
/// Describes a built-in function, one row of <see cref="SyntaxFacts.Builtins"/>.
/// </summary>
/// <param name="Kind">The function.</param>
/// <param name="MinArguments">The fewest arguments the function takes.</param>
/// <param name="MaxArguments">The most arguments the function takes, or null when it takes any number.</param>
/// <param name="Takes">
/// What the function takes, as a message about a wrong call completes <c>`.name` takes …</c>,
/// such as <c>one value</c>. It is null for a function whose wrong calls have a diagnostic of
/// their own.
/// </param>
/// <param name="Arithmetic">
/// Whether the function is arithmetic on its arguments and asks nothing about the program. These
/// are the functions a build's condition may call, since the configuration alone can evaluate
/// them.
/// </param>
/// <param name="MacroOnly">
/// Whether only a macro body may call the function, because it asks about the arguments the macro
/// was given.
/// </param>
public sealed record BuiltinFunction(
    BuiltinKind Kind, int MinArguments, int? MaxArguments, string? Takes, bool Arithmetic = false, bool MacroOnly = false)
{
    /// <summary>Gets the function's name as it is spelled in source, such as <c>.sizeof</c>.</summary>
    public string Name => SyntaxFacts.TextOf(Kind);

    /// <summary>Checks whether the function takes <paramref name="count"/> arguments.</summary>
    public bool Accepts(int count) => count >= MinArguments && (MaxArguments is not { } most || count <= most);
}
