namespace Norristown.Syntax;

// Hand-written members that Syntax.xml cannot describe. The rest of the class, and its
// summary, are generated.
public sealed partial class CallExpressionSyntax
{
    /// <summary>
    /// Gets the built-in function that <see cref="Function"/> names, or
    /// <see cref="BuiltinKind.None"/> for a call to a <c>.func</c> or a charmap.
    /// </summary>
    public BuiltinKind BuiltinKind => Function is { } function ? SyntaxFacts.BuiltinKindOf(function.Text) : BuiltinKind.None;
}
