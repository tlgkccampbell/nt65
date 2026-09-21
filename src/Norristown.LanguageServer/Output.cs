using Norristown.Emit;
using Norristown.Project;

namespace Norristown.LanguageServer;

/// <summary>
/// What a file became, for the view beside it. The text is the ca65 <c>nt65 build</c> writes,
/// as the program stands in the editor now, and the runs beside it are the line map that build
/// writes, read the way a caret needs it: which lines of the output each line of the source
/// wrote, so that moving in either can point at the other.
/// </summary>
internal static class Output
{
    /// <summary>
    /// What <paramref name="path"/> became, or null when the program has no such file.
    /// </summary>
    /// <param name="analysis">The program the file belongs to.</param>
    /// <param name="project">The project it is built as, whose <c>out</c> names where output goes.</param>
    /// <param name="path">The logical path of the source.</param>
    /// <param name="uri">How the client names that source.</param>
    /// <param name="version">The revision the client holds of it, or null for one it has not opened.</param>
    public static Protocol.OutputResult? Of(
        ProgramAnalysis analysis, ProjectSettings project, string path, string uri, int? version)
    {
        if (OutputPreview.Of(analysis, project, path) is not { } preview)
            return null;
        return new Protocol.OutputResult(
            uri, preview.Path, version, preview.Text, Runs(preview.SourceLines),
            Header(preview.SourceLines), preview.Note);
    }

    /// <summary>
    /// The line map as runs: each line of the source with the run of output lines it wrote, in
    /// output order. Lines the output generates nothing traceable for are in no run, and a
    /// source line that wrote lines in two places has a run for each.
    /// </summary>
    private static IReadOnlyList<Protocol.OutputRun> Runs(IReadOnlyList<int> sources)
    {
        var runs = new List<Protocol.OutputRun>();
        for (var i = 0; i < sources.Count; i++)
        {
            if (sources[i] == 0)
                continue;
            var last = i;
            while (last + 1 < sources.Count && sources[last + 1] == sources[i])
                last++;

            // Both ends count from zero, as a client's own lines do; the map counts from one.
            runs.Add(new Protocol.OutputRun(sources[i] - 1, i, last));
            i = last;
        }
        return runs;
    }

    /// <summary>
    /// How many lines the output opens with that no line of the source wrote: the note on an
    /// incomplete one, the <c>.feature</c> block and what the file exports and imports. A view
    /// opens past them, because they are there for ca65 rather than for the reader.
    /// </summary>
    private static int Header(IReadOnlyList<int> sources)
    {
        var at = 0;
        while (at < sources.Count && sources[at] == 0)
            at++;
        return at;
    }
}
