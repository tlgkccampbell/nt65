using Norristown.Syntax;

namespace Norristown.Tests.Syntax;

public sealed class DesignCorpusTests
{
    [Fact]
    public void EveryCodeBlockInTheDesignIsTagged()
    {
        Assert.True(DesignCorpus.UntaggedFences.Count == 0,
            "tag these DESIGN.md code fences (nt65, ca65, text, json): lines " + string.Join(", ", DesignCorpus.UntaggedFences));
        Assert.True(DesignCorpus.Blocks.Count > 30);
    }

    /// <summary>Every nt65 example lexes without errors, has balanced blocks and round-trips exactly.</summary>
    [Fact]
    public void EveryNt65CodeBlockLexesAndBalances()
    {
        var failures = Repo.CollectFailures(DesignCorpus.Blocks, block =>
        {
            var tree = SyntaxTree.Parse("DESIGN.md", block.Text);
            var problems = tree.Diagnostics
                .Select(d => $"{block}: example line {d.Span.Line}: {d.Message}")
                .ToList();
            if (tree.Root.ToFullString() != block.Text)
                problems.Add($"{block}: the tree does not give back its text");
            return problems;
        });
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
