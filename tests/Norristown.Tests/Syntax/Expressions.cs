using Norristown.Syntax;

namespace Norristown.Tests.Syntax;

/// <summary>Parses expressions on their own, for tests of how an expression reads.</summary>
internal static class Expressions
{
    /// <summary>
    /// Returns the operand of a <c>.word</c> holding <paramref name="expression"/>, which is the
    /// shortest line an expression fits on.
    /// </summary>
    public static SyntaxNode Parse(string expression)
    {
        var line = SyntaxTree.Parse("test.nt65", ".word " + expression).Root.DescendantNodes().OfType<LineSyntax>().First();
        var directive = Assert.IsType<DataDirectiveSyntax>(line.Statement);
        return Assert.Single(Assert.IsType<InlineDataSyntax>(directive.Tail).Values);
    }
}
