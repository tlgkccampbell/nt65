namespace Norristown.Emit;

/// <summary>
/// Represents one line of the output before it becomes text. Two of the emitter's passes work
/// on lines rather than on statements. One lines up a run of named data lines on their
/// directives once the whole run is known, and the other turns a run of one repeated byte into
/// the equivalent <c>.res</c>. Neither can read what it needs from a line that is already text.
/// <para>
/// What the line assembles to and where it came from are fields of the line rather than two
/// lists kept beside it. The line map is emitted from them, and a line that is removed has to
/// take them with it.
/// </para>
/// </summary>
/// <param name="Text">
/// The line's content, indented to its position, without the name in front of it or the
/// comment beside it.
/// </param>
/// <param name="Bytes">
/// The number of bytes ca65 assembles the line to, or
/// <see cref="Layout.DataLengths.Unpredictable"/> for a line nt65 makes no claim about.
/// </param>
/// <param name="Source">
/// The line of its module's source it came from, counting from one, or 0 for a line the map
/// should not point at any source line.
/// </param>
/// <param name="Label">
/// The name at the margin, for a named data line, which shares its line with what it names.
/// Null for every other line, including a label on a line of its own.
/// </param>
/// <param name="Comment">The comment nt65 adds about the line, emitted at a column of its own.</param>
/// <param name="File">
/// The index of the translation unit's source that <see cref="Source"/> counts in. It is 0 for
/// the module the output is named after, and one more for each module placed in it, in the
/// order they are emitted.
/// </param>
internal sealed record EmittedLine(
    string Text, int Bytes = 0, int Source = 0, string? Label = null, string? Comment = null, int File = 0)
{
    /// <summary>Where a generated comment starts, so that a column of them lines up.</summary>
    private const int CommentColumn = 36;

    /// <summary>Returns the text with its generated comment at the column where comments line up.</summary>
    public static string Commented(string text, string? comment) =>
        comment is null ? text : text + new string(' ', Math.Max(CommentColumn - text.Length, 2)) + "; " + comment;
}
