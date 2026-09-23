using System.Text.RegularExpressions;
using Norristown.Syntax;

namespace Norristown.Tests.Fixtures;

/// <summary>
/// Every nt65 source in the repository is in the standard layout, and formatting produces that
/// layout whatever whitespace the file started with. The formatter has nothing to choose: a
/// line's indentation is determined by the braces around it, so formatting a file with no
/// indentation at all, or with far too much, gives back the checked-in file character for
/// character.
/// <para>
/// That is also the whole proof that formatting changes no meaning. The snapshots under
/// <c>expected</c> are the output of the sources as they are checked in, so a formatter that
/// changed anything but whitespace would change one of them.
/// </para>
/// </summary>
public sealed partial class FormattingTests
{
    [Fact]
    public void EveryNt65SourceIsFormatted()
    {
        var failures = Repo.CollectFailures(Repo.Sources(), file =>
        {
            var text = Repo.ReadText(file);
            return Formatter.Format(SyntaxTree.Parse(file, text)) == text
                ? []
                : new[] { $"{Repo.Named(file)} is not formatted; `nt65 fmt` writes it" };
        });
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void LayingASourceOutFromAnyWhitespaceAtAllGivesItBack()
    {
        var failures = Repo.CollectFailures(Repo.Sources(), file =>
        {
            var text = Repo.ReadText(file);
            return Manglings(text)
                .Select(mangled => Formatter.Format(SyntaxTree.Parse(file, mangled)))
                .Where(laid => laid != text)
                .Select(laid => $"{Repo.Named(file)}, written another way, does not lay out as it is written:\n"
                    + Difference(text, laid));
        });
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// The two properties that must hold for a file nobody has finished typing: the syntax tree
    /// reproduces its text exactly, every token of every line included, and formatting is
    /// idempotent — formatting the formatted text changes nothing. Checked over every source in the
    /// repository and every way <see cref="BrokenLines"/> cuts its lines short, because a
    /// half-written file is what an editor usually formats.
    /// </summary>
    [Fact]
    public void AHalfWrittenSourceReadsBackWholeAndLaysOutOnce()
    {
        var failures = Repo.CollectFailures(Repo.Sources(), file =>
        {
            var name = Repo.Named(file);
            var problems = new List<string>();
            var cut = 0;
            foreach (var variant in BrokenLines.Variants(Repo.ReadText(file)))
            {
                var tree = SyntaxTree.Parse(name, variant);
                problems.AddRange(Syntax.Fidelity.Problems(tree).Select(problem => $"{name} cut {cut}: {problem}"));

                var once = Formatter.Format(tree);
                if (Formatter.Format(SyntaxTree.Parse(name, once)) != once)
                    problems.Add($"{name} cut {cut}: laying it out again does not give what laying it out gave");
                cut++;
            }
            return problems;
        });
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }

    /// <summary>
    /// The same file with its whitespace changed: with no indentation; with tabs and spaces
    /// before and after every line; and with the gap closed up where a run of data lines aligns
    /// its directives in a column. Line breaks are left alone, because the formatter keeps the
    /// ones a file was written with.
    /// </summary>
    private static IEnumerable<string> Manglings(string text)
    {
        var flat = Indent().Replace(text, "");
        yield return flat;
        yield return Line().Replace(flat, written =>
            written.Length == 0 ? "" : "\t   " + written.Value + "  \t");
        yield return Column().Replace(flat, ": ");
    }

    /// <summary>The first line that differs, which shows what the formatter changed.</summary>
    private static string Difference(string expected, string actual)
    {
        string[] want = expected.ReplaceLineEndings("\n").Split('\n'), got = actual.ReplaceLineEndings("\n").Split('\n');
        for (var i = 0; i < Math.Max(want.Length, got.Length); i++)
        {
            if (i >= want.Length || i >= got.Length || want[i] != got[i])
            {
                return $"  line {i + 1} is: {(i < want.Length ? want[i] : "<end>")}\n"
                    + $"  laid out as: {(i < got.Length ? got[i] : "<end>")}";
            }
        }
        return "  nothing differs line by line, so the line breaks do";
    }

    /// <summary>A line's leading whitespace, which the formatter is free to choose.</summary>
    [GeneratedRegex(@"^[ \t]+", RegexOptions.Multiline)]
    private static partial Regex Indent();

    /// <summary>One line's own text, without the break that ends it.</summary>
    [GeneratedRegex(@"^[^\r\n]*", RegexOptions.Multiline)]
    private static partial Regex Line();

    /// <summary>The gap between a declaration's <c>:</c> and its directive, where a run of lines aligns the directives in a column.</summary>
    [GeneratedRegex(@":[ \t]{2,}(?=\.)")]
    private static partial Regex Column();
}
