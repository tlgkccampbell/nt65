using Norristown.Emit;
using Norristown.Project;

namespace Norristown.LanguageServer;

/// <summary>
/// Builds the output a source file produces, for the side-by-side output view. The text is the
/// ca65 source that <c>nt65 build</c> would write from the program as it stands in the editor
/// now. The runs are that build's line map arranged for caret tracking. They record which output
/// lines each source line produced, so that moving the caret in either view can highlight the
/// other.
/// </summary>
internal static class Output
{
    /// <summary>
    /// Returns the output that <paramref name="path"/> produces, or null when the program has no
    /// such file.
    /// </summary>
    /// <param name="analysis">The analysis of the program the file belongs to.</param>
    /// <param name="project">
    /// The settings of the project the file is built in, whose <c>out</c> names where output goes.
    /// </param>
    /// <param name="path">The logical path of the source.</param>
    /// <param name="uri">The client's URI for the source.</param>
    /// <param name="version">
    /// The version of the source the client holds, or null for a file it has not opened.
    /// </param>
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
    /// Returns the line map as runs, each pairing a source line with the run of output lines it
    /// produced, in output order. Output lines that no source line produced belong to no run, and
    /// a source line that produced lines in two places has a run for each.
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
    /// Returns the number of lines at the start of the output that no source line produced. These
    /// are the note on incomplete output, the <c>.feature</c> block, and the file's exports and
    /// imports. A view opens past them, because they are there for ca65 rather than for the
    /// reader.
    /// </summary>
    private static int Header(IReadOnlyList<int> sources)
    {
        var at = 0;
        while (at < sources.Count && sources[at] == 0)
            at++;
        return at;
    }
}
