namespace Norristown.Emit;

/// <summary>
/// One line of the output, before it becomes text. Two of the emitter's passes work on lines
/// rather than on statements — a run of named data lines is lined up on its directives once
/// the whole run is known, and a run of one repeated byte becomes the <c>.res</c> that says
/// the same thing — and neither can read what it needs out of a line that is already text.
/// <para>
/// What the line assembles to and where it came from are parts of it rather than two lists
/// kept beside it: the line map is written from them, and a line taken away has to take them
/// with it.
/// </para>
/// </summary>
/// <param name="Text">
/// What the line says, indented where it goes, without the name in front of it or the
/// comment beside it.
/// </param>
/// <param name="Bytes">
/// What ca65 assembles it to, or <see cref="Layout.DataLengths.Unpredictable"/> for a line
/// nt65 makes no claim about.
/// </param>
/// <param name="Source">
/// The line of its module's source it came from, counting from one, or 0 for a line the map
/// should not point at any source line.
/// </param>
/// <param name="Label">
/// The name at the margin, for a named data line, which shares its line with what it names.
/// Null for every other line, including a label written on a line of its own.
/// </param>
/// <param name="Comment">What nt65 says about the line, written at a column of its own.</param>
/// <param name="File">
/// Which of the translation unit's sources <see cref="Source"/> counts in: 0 for the module the
/// output is named after, and one more for each module placed in it, in the order they are written.
/// </param>
internal sealed record EmittedLine(
    string Text, int Bytes = 0, int Source = 0, string? Label = null, string? Comment = null, int File = 0);
