using System.Globalization;

namespace Norristown.Emit;

/// <summary>
/// One module's ca65 as the program stands, for showing beside the source it came from and for
/// <c>nt65 build --stdout</c>. It is the file a build writes, with one difference: a build
/// writes nothing for a program that is wrong, and this writes what the emitter got to anyway,
/// under a first line saying that it is incomplete and why.
/// <para>
/// A module another places has no file of its own: what it became is its part of the file its
/// translation unit is written as, from the comment that opens the part to the one that closes
/// it, what it places in turn included. What it exports and imports is at the top of that file,
/// with every other module's.
/// </para>
/// <para>
/// Where each line came from is carried beside the text rather than in it, as it is in the map
/// a build writes: the ca65 shown is the ca65 that would be assembled, and not a listing with
/// the source numbered through it.
/// </para>
/// </summary>
/// <param name="Path">Where the file goes, relative to the project root: for a placed module, the file its part is in.</param>
/// <param name="Source">The logical path of the source it was written from.</param>
/// <param name="Text">The ca65, always with <c>\n</c> line endings.</param>
/// <param name="SourceLines">
/// Which line of <see cref="Source"/> each line of <see cref="Text"/> came from, counting from
/// one, or 0 for a line that came from nowhere the source wrote — the header, the blank lines
/// between routines, and the note on an incomplete one.
/// </param>
/// <param name="Note">Why the output is incomplete, or null when the program is not wrong.</param>
public sealed record OutputPreview(
    string Path, string Source, string Text, IReadOnlyList<int> SourceLines, string? Note)
{
    /// <summary>
    /// The output for one file of <paramref name="analysis"/>, or null when the program has no
    /// such file. <paramref name="project"/> is what it is built as, whose <c>out</c> names
    /// where the file goes.
    /// </summary>
    public static OutputPreview? Of(ProgramAnalysis analysis, Project.ProjectSettings project, string path)
    {
        if (Compiler.EmitFile(analysis, project, path) is not { } output)
            return null;
        var (text, lines) = PartOf(output, path);
        var note = Incomplete(analysis, path);
        return note is null
            ? new OutputPreview(output.Path, path, text, lines, null)
            : new OutputPreview(output.Path, path, $"; {note}\n{text}", [0, .. lines], note);
    }

    /// <summary>
    /// The part of <paramref name="output"/> <paramref name="path"/> wrote, and which of that
    /// source's lines each of its lines came from: the whole file for the module it is named
    /// after, and the lines of its part for a module placed in it. A line another module wrote
    /// came from nowhere this source did.
    /// </summary>
    private static (string Text, IReadOnlyList<int> Lines) PartOf(OutputFile output, string path)
    {
        if (output.Source == path)
            return (output.Text, output.LineSources);
        var index = output.Sources.ToList().FindIndex(source => source.Path == path);
        if (index < 0)
            return ("", []);
        var part = output.Sources[index];
        var all = output.Text.Split('\n');
        var text = string.Concat(all.Skip(part.First).Take(part.Count).Select(line => line + "\n"));
        var lines = Enumerable.Range(part.First, part.Count)
            .Select(i => i < output.LineFiles.Count && output.LineFiles[i] == index ? output.LineSources[i] : 0)
            .ToList();
        return (text, lines);
    }

    /// <summary>
    /// Why the output for <paramref name="path"/> is incomplete, or null when nothing is wrong.
    /// The answer comes before the working: what it is, then where the first of it is, and only
    /// then how many there are.
    /// </summary>
    private static string? Incomplete(ProgramAnalysis analysis, string path)
    {
        var errors = analysis.Diagnostics.Where(d => d.Severity == Severity.Error).ToList();
        if (errors.Count == 0)
            return null;

        // The file's own errors are the ones the person looking at it can fix, so they are the
        // ones named; where all of them are elsewhere, the file they are in is the news.
        var own = errors.Where(d => d.Span.File == path).ToList();
        var first = own.Count > 0 ? own[0] : errors[0];
        var where = first.Span.File == path
            ? string.Create(CultureInfo.InvariantCulture, $"line {first.Span.Line}")
            : string.Create(CultureInfo.InvariantCulture, $"{first.Span.File} line {first.Span.Line}");
        var rest = errors.Count == 1
            ? ""
            : string.Create(CultureInfo.InvariantCulture, $", and {errors.Count - 1} more");
        return $"nt65: incomplete, because the program is wrong. {where}: {first.Message}{rest}";
    }
}
