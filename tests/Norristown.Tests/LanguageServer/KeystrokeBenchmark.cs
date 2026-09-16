using System.Diagnostics;
using Norristown.LanguageServer;
using Norristown.LanguageServer.Protocol;

// The protocol has a Range of its own, which is the one these tests mean.
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// What an edit costs: the edit, and the analysis the diagnostics are published from. Not part
/// of the edit loop; <c>scripts/test.ps1 -Benchmark</c> runs it, and the numbers mean most from
/// a Release build.
/// </summary>
public sealed class KeystrokeBenchmark(ITestOutputHelper output)
{
    private const int Files = 300;

    [Fact]
    [Trait("Category", "Benchmark")]
    public void EditsInAProgramOfManyFiles()
    {
        var workspace = new Workspace();
        for (var i = 0; i < Files; i++)
            workspace.Open(new TextDocumentItem(GeneratedProject.Uri(i), "nt65", 1, GeneratedProject.Text(i, Files)));

        var watch = Stopwatch.StartNew();
        var first = workspace.Analysis();
        output.WriteLine($"{Files} files, first analysis: {watch.Elapsed.TotalMilliseconds:0} ms");
        Assert.Empty(first.Diagnostics);

        // In the middle file: `lda #0` becomes `lda #1` and back; a line is added above it and
        // taken away; and the file's exported size changes, which the file after it uses.
        var uri = GeneratedProject.Uri(Files / 2);
        var version = 1;
        Time(workspace, uri, ref version, "keystroke in a routine body", Line(workspace, uri, "lda #0"), 9, 10, "1", "0");
        Time(workspace, uri, ref version, "new line in a routine body", Line(workspace, uri, "lda #0"), 0, 0, "\n", null);
        Time(workspace, uri, ref version, "exported constant changed", Line(workspace, uri, "_SIZE = "), 12, 14, "17", "32");
    }

    /// <summary>One file of many routines, which is what rerunning only the edited routine would save on.</summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void AKeystrokeInALargeFile()
    {
        var text = GeneratedProject.Text(0, 1) + string.Concat(Enumerable.Range(0, 200).Select(i => $$"""
            .proc big{{i}}: a8, i8 {
                ldx #0
            @loop:
                lda m000_table,x
                sta m000_state,x
                inx
                cpx #8
                bne @loop
                jsr m000_step
                m000_put!(m000_count, {{i}})
                rts
            }

            """.ReplaceLineEndings("\n")));
        var workspace = new Workspace();
        var uri = GeneratedProject.Uri(0);
        workspace.Open(new TextDocumentItem(uri, "nt65", 1, text));
        output.WriteLine($"{text.Count(c => c == '\n')} lines in one file");
        Assert.Empty(workspace.Analysis().Diagnostics);

        var version = 1;
        Time(workspace, uri, ref version, "keystroke in a routine body", Line(workspace, uri, "cpx #8"), 9, 10, "9", "8");
    }

    private static int Line(Workspace workspace, string uri, string find)
    {
        var text = workspace.Find(uri)!.Tree.Text;
        return text[..text.IndexOf(find, StringComparison.Ordinal)].Count(c => c == '\n');
    }

    /// <summary>
    /// Thirty edits, timed. The even ones write <paramref name="text"/> over the columns given;
    /// the odd ones undo it, putting back <paramref name="undo"/>, or joining the line up again
    /// when there is none.
    /// </summary>
    private void Time(
        Workspace workspace, string uri, ref int version, string what, int line, int start, int end, string text, string? undo)
    {
        var times = new List<double>();
        var reanalyzed = new List<int>();
        for (var i = 0; i < 30; i++)
        {
            var (range, written) = i % 2 == 0
                ? (new Range(new Position(line, start), new Position(line, end)), text)
                : undo is null
                    ? (new Range(new Position(line, 0), new Position(line + 1, 0)), "")
                    : (new Range(new Position(line, start), new Position(line, start + text.Length)), undo);
            var watch = Stopwatch.StartNew();
            workspace.Change(new VersionedTextDocumentIdentifier(uri, ++version),
                [new TextDocumentContentChangeEvent(range, written)]);
            var analysis = workspace.Analysis();
            _ = analysis.DiagnosticsFor(workspace.Find(uri)!.Tree.Path);
            times.Add(watch.Elapsed.TotalMilliseconds);
            reanalyzed.Add(analysis.Reanalyzed);
            Assert.Empty(analysis.Diagnostics);
        }
        times.Sort();
        output.WriteLine($"{what}: median {times[times.Count / 2]:0.0} ms, min {times[0]:0.0} ms, "
            + $"max {times[^1]:0.0} ms; files analyzed {reanalyzed.Min()} to {reanalyzed.Max()}");
    }
}
