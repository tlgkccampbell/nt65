using System.Globalization;

namespace Norristown.Emit;

/// <summary>
/// Represents one module's ca65 output as the program stands, for showing beside the source it
/// came from and for <c>nt65 build --stdout</c>. It is the file a build writes, with one
/// difference. A build writes nothing for a program with errors, but this preview shows what
/// the emitter produced anyway, under a first line saying that it is incomplete and why.
/// <para>
/// A module that another module places has no file of its own. Its output is its part of its
/// translation unit's file, from the comment that opens that part to the comment that closes
/// it, including any modules it places in turn. Its exports and imports are at the top of that
/// file, along with every other module's.
/// </para>
/// <para>
/// Where each line came from is carried beside the text rather than in it, as it is in the map
/// a build writes. The ca65 shown is the ca65 that would be assembled, not a listing with the
/// source numbered through it.
/// </para>
/// </summary>
/// <param name="Path">
/// Where the file goes, relative to the project root. For a placed module, this is the file its
/// part is in.
/// </param>
/// <param name="Source">The logical path of the source it was emitted from.</param>
/// <param name="Text">The ca65, always with <c>\n</c> line endings.</param>
/// <param name="SourceLines">
/// Which line of <see cref="Source"/> each line of <see cref="Text"/> came from, counting from
/// one, or 0 for a line with no counterpart in the source, such as the header, the blank lines
/// between routines, and the note at the top of incomplete output.
/// </param>
/// <param name="Note">Why the output is incomplete, or null when the program has no errors.</param>
public sealed record OutputPreview(
    string Path, string Source, string Text, IReadOnlyList<int> SourceLines, string? Note)
{
    /// <summary>The text shown for a module that a build writes no file for.</summary>
    private const string Nothing = "; No file: nothing this module declares reaches ca65, so a build writes none.\n";

    /// <summary>
    /// Returns the output for one file of <paramref name="analysis"/>, or null when the program
    /// has no such file. <paramref name="project"/> holds the settings it is built with, whose
    /// <c>out</c> names where the file goes.
    /// </summary>
    public static OutputPreview? Of(ProgramAnalysis analysis, Project.ProjectSettings project, string path)
    {
        if (Compiler.EmitFile(analysis, project, path) is not { } output)
            return null;

        // A build writes no file for a module that emits nothing past the header, and the
        // preview says so rather than showing the header of a file that does not exist.
        var (text, lines) = output.IsEmpty ? (Nothing, new[] { 0 }) : PartOf(output, path);
        var note = Incomplete(analysis, path);
        return note is null
            ? new OutputPreview(output.Path, path, text, lines, null)
            : new OutputPreview(output.Path, path, $"; {note}\n{text}", [0, .. lines], note);
    }

    /// <summary>
    /// Returns the part of <paramref name="output"/> emitted for the source
    /// <paramref name="path"/>, and the line of that source each of its lines came from. The part
    /// is the whole file for the module the file is named after, and the lines of the module's
    /// part for a module placed in it. A line emitted for another module maps to 0.
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
    /// Returns why the output for <paramref name="path"/> is incomplete, or null when there are
    /// no errors. The note leads with the conclusion. It says first that the output is
    /// incomplete, then how many errors the program has, then where the first of them is and
    /// what it says.
    /// </summary>
    private static string? Incomplete(ProgramAnalysis analysis, string path)
    {
        var errors = analysis.Diagnostics.Where(d => d.Severity == Severity.Error).ToList();
        if (errors.Count == 0)
            return null;

        // The file's own errors are the ones the person looking at it can fix, so they are the
        // ones named. When all of them are in other files, the note names the file of the first.
        var own = errors.Where(d => d.Span.File == path).ToList();
        var first = own.Count > 0 ? own[0] : errors[0];
        var where = first.Span.File == path
            ? string.Create(CultureInfo.InvariantCulture, $"line {first.Span.Line}")
            : string.Create(CultureInfo.InvariantCulture, $"{first.Span.File} line {first.Span.Line}");
        var count = errors.Count == 1
            ? "an error"
            : string.Create(CultureInfo.InvariantCulture, $"{errors.Count} errors, including one");
        return $"nt65: this output is incomplete because the program has {count} at {where}: {first.Message}";
    }
}
