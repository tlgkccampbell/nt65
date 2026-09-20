namespace Norristown.Syntax;

// The half of the node the table cannot say: finding the [i] written after one name of a path.
// The rest of the class, and its summary, are generated.
public sealed partial class NameExpressionSyntax
{
    /// <summary>The <c>[i]</c> written straight after <paramref name="name"/>, one of <see cref="Names"/>, or null.</summary>
    /// <param name="name">The name to look after.</param>
    public ElementIndexSyntax? IndexAfter(SyntaxToken name) => NodeAfter(name) as ElementIndexSyntax;
}
