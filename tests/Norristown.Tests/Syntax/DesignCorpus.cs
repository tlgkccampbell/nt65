using System.Text;

namespace Norristown.Tests.Syntax;

/// <summary>The nt65 code blocks of DESIGN.md (fences tagged <c>nt65</c>).</summary>
internal static class DesignCorpus
{
    public static readonly IReadOnlyList<CodeBlock> Blocks = Load();

    /// <summary>Opening fences with no language tag, which the corpus would silently skip.</summary>
    public static readonly IReadOnlyList<int> UntaggedFences = FindUntagged();

    private static string[] ReadLines() => Repo.ReadText(Repo.Path("DESIGN.md")).ReplaceLineEndings("\n").Split('\n');

    private static List<CodeBlock> Load()
    {
        var lines = ReadLines();
        var blocks = new List<CodeBlock>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i] != "```nt65")
                continue;
            var start = i + 1;
            var body = new StringBuilder();
            for (i++; lines[i] != "```"; i++)
            {
                // `...` stands for elided code in the design's examples.
                body.Append(lines[i].Trim() == "..." ? "" : lines[i]).Append('\n');
            }
            blocks.Add(new CodeBlock(start + 1, body.ToString()));
        }
        return blocks;
    }

    private static List<int> FindUntagged()
    {
        var lines = ReadLines();
        var untagged = new List<int>();
        var inside = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("```", StringComparison.Ordinal))
                continue;
            if (!inside && lines[i] == "```")
                untagged.Add(i + 1);
            inside = !inside;
        }
        return untagged;
    }

    public sealed record CodeBlock(int Line, string Text)
    {
        public override string ToString() => $"DESIGN.md:{Line}";
    }
}
