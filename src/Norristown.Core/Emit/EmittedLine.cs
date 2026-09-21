namespace Norristown.Emit;

/// <summary>
/// One line of the output before it is a string. Two of the emitter's passes work on lines
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
/// The line of this file's source it came from, counting from one, or 0 for one that came
/// from nowhere the map should name.
/// </param>
/// <param name="Label">
/// The name at the margin, for a named data line, which shares its line with what it names.
/// Null for every other line, the ones whose name stands on a line of its own included.
/// </param>
/// <param name="Comment">What nt65 says about the line, written at a column of its own.</param>
internal sealed record EmittedLine(
    string Text, int Bytes = 0, int Source = 0, string? Label = null, string? Comment = null);
