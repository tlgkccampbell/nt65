using System.Diagnostics;
using Norristown.LanguageServer;
using Norristown.LanguageServer.Protocol;

// The protocol defines its own Range type, and these tests use that one.
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Measures what an edit costs, in the two stages in which the server publishes diagnostics.
/// The first stage is what the typist waits for. It covers applying the edit, the analysis and
/// the edited file's diagnostics, which are sent at once. The second stage is the whole
/// program's diagnostics, which are sent once typing has stopped. The benchmark is not part of
/// the everyday test run. <c>scripts/test.ps1 -Benchmark</c> runs it, and the numbers mean most
/// from a Release build.
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
        var first = workspace.AnalysisFor(Workspace.PathOf(GeneratedProject.Uri(0)));
        output.WriteLine($"{Files} files, first analysis: {watch.Elapsed.TotalMilliseconds:0} ms");
        Assert.Empty(first.Diagnostics);

        // In the middle file, `lda #0` becomes `lda #1` and back, a line is added above it and
        // removed, and the file's exported size changes, which the file after it uses. Then the
        // last file's size changes. The first file's `M000_LIMIT` is computed from it, and every
        // file uses that constant.
        var uri = GeneratedProject.Uri(Files / 2);
        var version = 1;
        Time(workspace, uri, ref version, "keystroke in a routine body", Line(workspace, uri, "lda #0"), 9, 10, "1", "0");
        Time(workspace, uri, ref version, "new line in a routine body", Line(workspace, uri, "lda #0"), 0, 0, "\n", null);
        Time(workspace, uri, ref version, "exported constant changed", Line(workspace, uri, "_SIZE = "), 12, 14, "17", "32");
        uri = GeneratedProject.Uri(Files - 1);
        version = 1;
        Time(workspace, uri, ref version, "constant every file uses changed", Line(workspace, uri, "_SIZE = "), 12, 14, "17", "27");
    }

    /// <summary>
    /// Measures a keystroke in a single file of many routines, which is the case that
    /// reanalyzing only the edited routine would speed up.
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void AKeystrokeInALargeFile()
    {
        // Each routine is exported, as a real module's routines are. A routine that nothing calls
        // or exports gets a warning, and this benchmark measures the cost of the file's size, not
        // the cost of that warning.
        var text = GeneratedProject.Text(0, 1) + string.Concat(Enumerable.Range(0, 200).Select(i => $$"""
            .export .proc big{{i}}: a8, i8 {
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
        Assert.Empty(workspace.AnalysisFor(Workspace.PathOf(uri)).Diagnostics);

        var version = 1;
        Time(workspace, uri, ref version, "keystroke in a routine body", Line(workspace, uri, "cpx #8"), 9, 10, "9", "8");
    }

    private static double Median(List<double> sorted) => sorted[sorted.Count / 2];

    private static int Line(Workspace workspace, string uri, string find)
    {
        var text = workspace.Find(uri)!.Tree.Text;
        return text[..text.IndexOf(find, StringComparison.Ordinal)].Count(c => c == '\n');
    }

    /// <summary>
    /// Times thirty edits. Each even-numbered edit replaces the given columns with
    /// <paramref name="text"/>. Each odd-numbered edit undoes it by putting back
    /// <paramref name="undo"/>, or by joining the line up again when <paramref name="undo"/> is
    /// null.
    /// </summary>
    private void Time(
        Workspace workspace, string uri, ref int version, string what, int line, int start, int end, string text, string? undo)
    {
        var atOnce = new List<double>();
        var settled = new List<double>();
        var analyzed = new SortedSet<string>(StringComparer.Ordinal);
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

            // The typist waits for the edited file's own diagnostics, from the analysis the edit
            // requested.
            var own = workspace.ToPublish(uri)!.Value;
            _ = Lsp.ToDiagnostics(own.Diagnostics, own.Tree, own.Configuration);
            atOnce.Add(watch.Elapsed.TotalMilliseconds);

            var analysis = workspace.AnalysisFor(Workspace.PathOf(uri));
            foreach (var file in workspace.ToPublish())
                _ = Lsp.ToDiagnostics(file.Diagnostics, file.Tree, file.Configuration);
            settled.Add(watch.Elapsed.TotalMilliseconds);
            analyzed.Add(analysis.WholeProgram is { } reason ? $"all ({reason})" : $"{analysis.Reanalyzed} file(s)");
            Assert.Empty(analysis.Diagnostics);
        }
        atOnce.Sort();
        settled.Sort();
        output.WriteLine($"{what}: at once median {Median(atOnce):0.0} ms (min {atOnce[0]:0.0}, max {atOnce[^1]:0.0}); "
            + $"whole program median {Median(settled):0.0} ms (min {settled[0]:0.0}, max {settled[^1]:0.0}); "
            + $"analyzed {string.Join(", ", analyzed)}");
    }
}
