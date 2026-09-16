namespace Norristown;

/// <summary>
/// One generated ca65 file. <see cref="Text"/> always uses <c>\n</c> line endings.
/// </summary>
/// <param name="Path">Where it goes, relative to the project root.</param>
/// <param name="Text">The ca65 source.</param>
/// <param name="LineBytes">
/// How many bytes nt65 expects each line of <see cref="Text"/> to assemble to (§7.6), one
/// entry per line. These are the lengths the analysis works from, so comparing them with
/// what ca65 actually generates is what says the two agree.
/// </param>
public sealed record OutputFile(string Path, string Text, IReadOnlyList<int> LineBytes)
{
    /// <summary>A file whose lengths nothing has worked out, such as one a test wrote by hand.</summary>
    public OutputFile(string path, string text) : this(path, text, []) { }
}
