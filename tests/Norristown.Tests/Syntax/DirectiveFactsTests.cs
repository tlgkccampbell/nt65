using Norristown.Syntax;

namespace Norristown.Tests.Syntax;

/// <summary>
/// Checks that every <see cref="DirectiveKind"/> has the facts the parser, the binder and the
/// editor read about it, so that a directive added to the enum cannot be missing from the table.
/// </summary>
public sealed class DirectiveFactsTests
{
    /// <summary>Every directive is lexed as its kind, in any letter case, and spelled back the same way.</summary>
    [Fact]
    public void EveryDirectiveIsReadAndSpelledAsItsKind()
    {
        foreach (var kind in SyntaxFacts.Directives)
        {
            var text = SyntaxFacts.TextOf(kind);
            Assert.Equal(kind, SyntaxFacts.DirectiveKindOf(text));
            Assert.Equal(kind, SyntaxFacts.DirectiveKindOf(text.ToUpperInvariant()));
            var token = SyntaxTree.Parse("case.nt65", text + "\n").GetLine(0).Tokens[0];
            Assert.Equal(SyntaxKind.Directive, token.Kind);
            Assert.Equal(kind, token.DirectiveKind);
        }
    }

    /// <summary>
    /// Every directive parses to a statement, and every one may begin a line somewhere, except the
    /// two that continue an <c>.if</c> and so never begin a line of their own.
    /// </summary>
    [Fact]
    public void EveryDirectiveHasPlacementFacts()
    {
        foreach (var kind in SyntaxFacts.Directives)
        {
            Assert.NotEqual(SyntaxKind.None, SyntaxFacts.LineDirectiveKind(kind));
            var placement = SyntaxFacts.PlacementOf(kind);
            Assert.Equal(kind is DirectiveKind.Else or DirectiveKind.ElseIf, placement.Contexts == DirectiveContexts.None);
        }
    }

    /// <summary>A built-in function and the <c>.mod</c> operator are spelled like directives but are none.</summary>
    [Fact]
    public void AFunctionIsNoDirective()
    {
        Assert.Equal(DirectiveKind.None, SyntaxFacts.DirectiveKindOf(".lobyte"));
        Assert.Equal(DirectiveKind.None, SyntaxFacts.DirectiveKindOf(".mod"));
        Assert.Equal(DirectiveKind.None, SyntaxFacts.DirectiveKindOf("byte"));
    }
}
