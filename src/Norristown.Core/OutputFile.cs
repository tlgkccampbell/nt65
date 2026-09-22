namespace Norristown;

/// <summary>
/// One generated ca65 file. <see cref="Text"/> always uses <c>\n</c> line endings.
/// </summary>
/// <param name="Path">Where it goes, relative to the project root.</param>
/// <param name="Text">The ca65 source.</param>
/// <param name="LineBytes">
/// How many bytes nt65 expects each line of <see cref="Text"/> to assemble to, one
/// entry per line, or -1 for a line whose length only the assembler settles, such as an
/// <c>.align</c>. These are the lengths the analysis works from, so comparing them with
/// what ca65 actually generates is what says the two agree.
/// </param>
public sealed record OutputFile(string Path, string Text, IReadOnlyList<int> LineBytes)
{
    /// <summary>
    /// The files what is written depends on, by logical path, sorted: the sources whose
    /// changing may change it, and the binaries it includes.
    /// </summary>
    public IReadOnlyList<string> Dependencies { get; init; } = [];

    /// <summary>What it is: the ca65 of a module, or the line map beside it.</summary>
    public OutputKind Kind { get; init; } = OutputKind.Ca65;

    /// <summary>The logical path of the source it was written from.</summary>
    public string Source { get; init; } = "";

    /// <summary>
    /// How many bytes that source is, which the line map records so that ld65's debug file can
    /// say the same, and a debugger can tell that the source has changed under it.
    /// </summary>
    public int SourceSize { get; init; }

    /// <summary>
    /// Which line of <see cref="Source"/> each line of <see cref="Text"/> came from, one entry
    /// per line, or 0 for a line that came from nowhere a debugger should name. This is what
    /// <see cref="Emit.LineMap"/> writes beside the output, in place of the <c>.dbg line</c>
    /// directives that would otherwise stand between every two lines of ca65.
    /// </summary>
    public IReadOnlyList<int> LineSources { get; init; } = [];

    /// <summary>
    /// Which of <see cref="Sources"/> each line's entry in <see cref="LineSources"/> counts in,
    /// one entry per line, or empty where every line came from <see cref="Source"/>.
    /// </summary>
    public IReadOnlyList<int> LineFiles { get; init; } = [];

    /// <summary>
    /// Every source the file was written from: <see cref="Source"/> first, and then each module
    /// placed in it, in the order it writes them. Empty where the file is one module's.
    /// </summary>
    public IReadOnlyList<OutputSource> Sources { get; init; } = [];

    /// <summary>The sources the file was written from, <see cref="Source"/> alone where it is one module's.</summary>
    public IReadOnlyList<OutputSource> AllSources =>
        Sources.Count > 0 ? Sources : [new OutputSource(Source, SourceSize, 0, LineBytes.Count)];

    /// <summary>A file whose lengths nothing has worked out, such as one a test wrote by hand.</summary>
    public OutputFile(string path, string text) : this(path, text, []) { }
}
