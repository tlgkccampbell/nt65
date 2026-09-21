using System.Text;

namespace Norristown.Tests.Syntax;

/// <summary>
/// The code blocks of the documents, DESIGN.md and the guide, by the language their fences are
/// tagged with.
/// </summary>
internal static class DesignCorpus
{
    public static readonly IReadOnlyList<string> Documents = ["DESIGN.md", "docs/GUIDE.md"];

    public static readonly IReadOnlyList<CodeBlock> Blocks = Load("nt65");

    /// <summary>The ca65 code blocks: output as nt65 writes it.</summary>
    public static readonly IReadOnlyList<CodeBlock> Ca65Blocks = Load("ca65");

    /// <summary>Opening fences with no language tag, which the corpus would silently skip.</summary>
    public static readonly IReadOnlyList<string> UntaggedFences = FindUntagged();

    private static string[] ReadLines(string document) =>
        Repo.ReadText(Repo.Path([.. document.Split('/')])).ReplaceLineEndings("\n").Split('\n');

    private static List<CodeBlock> Load(string tag)
    {
        var blocks = new List<CodeBlock>();
        foreach (var document in Documents)
        {
            var lines = ReadLines(document);
            for (var i = 0; i < lines.Length; i++)
            {
                // A fence indented inside a list is a fence: an example that slipped past this
                // because of where it stands would be one no fixture holds and nobody notices.
                if (lines[i].TrimStart() != "```" + tag)
                    continue;
                var start = i + 1;
                var body = new StringBuilder();
                for (i++; lines[i].TrimStart() != "```"; i++)
                {
                    // `...` stands for elided code in the examples.
                    body.Append(lines[i].Trim() == "..." ? "" : lines[i]).Append('\n');
                }
                blocks.Add(new CodeBlock(document, start + 1, body.ToString()));
            }
        }
        return blocks;
    }

    private static List<string> FindUntagged()
    {
        var untagged = new List<string>();
        foreach (var document in Documents)
        {
            var lines = ReadLines(document);
            var inside = false;
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith("```", StringComparison.Ordinal))
                    continue;
                if (!inside && lines[i] == "```")
                    untagged.Add($"{document}:{i + 1}");
                inside = !inside;
            }
        }
        return untagged;
    }

    public sealed record CodeBlock(string Document, int Line, string Text)
    {
        public override string ToString() => $"{Document}:{Line}";
    }
}
