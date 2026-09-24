using Norristown.LanguageServer;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Tests the suggestion on a line longer than the editor's setting, which marks the expression
/// the line-breaking refactoring would lay out.
/// </summary>
public sealed class LongLineTests
{
    private const string Long = "X = .select(1, 2, 3) + .select(4, 5, 6) + .select(7, 8, 9)\n";

    [Fact]
    public void ALongLineIsSuggestedOverItsExpression()
    {
        var tree = SyntaxTree.Parse("main.nt65", ".module main\n" + Long);

        var hint = Assert.Single(Lsp.ToDiagnostics([], tree, Configuration.Everything, 40));

        Assert.Equal("long-line", hint.Code);
        Assert.Equal(Norristown.LanguageServer.Protocol.DiagnosticSeverity.Hint, hint.Severity);
        Assert.Equal(1, hint.Range.Start.Line);
        Assert.Equal(Long.IndexOf('.', StringComparison.Ordinal), hint.Range.Start.Character);
    }

    [Theory]
    [InlineData(Long, 0)]
    [InlineData(Long, 100)]
    [InlineData("X = 1                                            ; a long comment, and nothing to break\n", 40)]
    [InlineData("X = .select(\n    1111111111 + 2222222222 + 3333333333 + 4444444444,\n    5)\n", 40)]
    public void NothingIsSuggestedWhereThereIsNothingToBreak(string line, int limit)
    {
        var tree = SyntaxTree.Parse("main.nt65", ".module main\n" + line);

        Assert.Empty(Lsp.ToDiagnostics([], tree, Configuration.Everything, limit));
    }
}
