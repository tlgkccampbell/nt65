using Norristown.Syntax.InternalSyntax;
using Norristown.Syntax;

namespace Norristown.Tests.Syntax;

public sealed class DesignCorpusTests
{
    [Fact]
    public void EveryCodeBlockInTheDocumentsIsTagged()
    {
        Assert.True(DesignCorpus.UntaggedFences.Count == 0,
            "tag these code fences (nt65, ca65, text, json): " + string.Join(", ", DesignCorpus.UntaggedFences));
        Assert.True(DesignCorpus.Blocks.Count > 30);
    }

    /// <summary>Every nt65 example lexes and parses without errors, has balanced blocks and round-trips exactly.</summary>
    [Fact]
    public void EveryNt65CodeBlockLexesAndBalances()
    {
        var failures = Repo.CollectFailures(DesignCorpus.Blocks, block =>
        {
            var tree = SyntaxTree.Parse(block.Document, block.Text);
            var problems = tree.Diagnostics
                .Select(d => $"{block}: example line {d.Span.Line}: {d.Message}")
                .ToList();
            if (tree.Root.ToFullString() != block.Text)
                problems.Add($"{block}: the tree does not give back its text");
            problems.AddRange(Fidelity.Problems(tree).Select(p => $"{block}: {p}"));
            return problems;
        });
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// Every nt65 example is part of a fixture or a corpus program, so each is analyzed and its
    /// output checked. An example's paragraphs, the runs of lines between blank lines and
    /// <c>...</c>, appear in order in one source, each line after the one before it; a fixture
    /// may write what an example leaves out around and between them. Lines compare by their
    /// tokens, so spacing and comments, such as a fixture's <c>;!</c>, may differ.
    /// </summary>
    [Fact]
    public void EveryNt65CodeBlockIsInAFixture()
    {
        var sources = new[] { Repo.Path("tests", "fixtures"), Repo.Path("tests", "corpus") }
            .SelectMany(root => Directory.GetFiles(root, "*.nt65", SearchOption.AllDirectories))
            .Select(path => TokenLines(Repo.ReadText(path)).Where(line => line.Length > 0).ToList())
            .ToList();

        var missing = DesignCorpus.Blocks
            .Where(block => !sources.Any(source => Contains(source, Paragraphs(block.Text))))
            .Select(block => $"{block}: no fixture or corpus program holds this example")
            .ToList();
        Assert.True(missing.Count == 0, string.Join("\n", missing));
    }

    /// <summary>
    /// Every ca65 example is what nt65 writes: its lines, one after another, in a fixture's
    /// expected output. The design leaves out blank lines.
    /// </summary>
    [Fact]
    public void EveryCa65CodeBlockIsFixtureOutput()
    {
        static List<string> Lines(string text) =>
            [.. text.ReplaceLineEndings("\n").Split('\n')
                .Select(line => line.TrimEnd())
                .Where(line => line.Length > 0)];

        var outputs = Directory.GetFiles(Repo.Path("tests", "fixtures"), "*.s", SearchOption.AllDirectories)
            .Select(path => Lines(Repo.ReadText(path)))
            .ToList();
        var missing = DesignCorpus.Ca65Blocks
            .Where(block => !outputs.Any(output => Contains(output, [Lines(block.Text)])))
            .Select(block => $"{block}: no fixture writes this output")
            .ToList();
        Assert.True(DesignCorpus.Ca65Blocks.Count > 0);
        Assert.True(missing.Count == 0, string.Join("\n", missing));
    }

    private static IEnumerable<string> TokenLines(string text) =>
        text.ReplaceLineEndings("\n").Split('\n').Select(line =>
            string.Join(' ', Lexer.LexLine(line).Tokens.Where(token => token.Kind != SyntaxKind.EndOfLine).Select(token => token.Text)));

    private static List<List<string>> Paragraphs(string text)
    {
        var paragraphs = new List<List<string>> { new() };
        foreach (var line in TokenLines(text))
        {
            if (line.Length > 0)
                paragraphs[^1].Add(line);
            else if (paragraphs[^1].Count > 0)
                paragraphs.Add([]);
        }
        return [.. paragraphs.Where(paragraph => paragraph.Count > 0)];
    }

    private static bool Contains(List<string> source, List<List<string>> paragraphs)
    {
        var from = 0;
        foreach (var paragraph in paragraphs)
        {
            var at = Enumerable.Range(from, Math.Max(0, source.Count - paragraph.Count - from + 1))
                .FirstOrDefault(start => source.Skip(start).Take(paragraph.Count).SequenceEqual(paragraph), -1);
            if (at < 0)
                return false;
            from = at + paragraph.Count;
        }
        return true;
    }
}
