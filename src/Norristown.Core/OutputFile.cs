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
/// what ca65 actually generates shows whether nt65 and ca65 agree.
/// </param>
public sealed record OutputFile(string Path, string Text, IReadOnlyList<int> LineBytes)
{
    /// <summary>
    /// The files this output depends on, as sorted logical paths: the sources whose changes may
    /// change it, and the binaries it includes.
    /// </summary>
    public IReadOnlyList<string> Dependencies { get; init; } = [];

    /// <summary>
    /// Whether the file holds nothing but its header, as the ca65 of a module that declares only
    /// charmaps, functions, lists, macros and the like, which cross modules by value. A build
    /// writes no file for it, since there would be nothing in it to assemble or link.
    /// </summary>
    public bool IsEmpty { get; init; }

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
    /// per line, or 0 for a line with no source line a debugger should point to. This is what
    /// <see cref="Emit.LineMap"/> writes beside the output, in place of the <c>.dbg line</c>
    /// directives that would otherwise have to sit between every two lines of ca65.
    /// </summary>
    public IReadOnlyList<int> LineSources { get; init; } = [];

    /// <summary>
    /// For each line, the index into <see cref="Sources"/> of the file its entry in
    /// <see cref="LineSources"/> refers to; empty when every line came from <see cref="Source"/>.
    /// </summary>
    public IReadOnlyList<int> LineFiles { get; init; } = [];

    /// <summary>
    /// Every source the file was written from: <see cref="Source"/> first, and then each module
    /// placed in it, in the order they are written. Empty when the file holds only one module.
    /// </summary>
    public IReadOnlyList<OutputSource> Sources { get; init; } = [];

    /// <summary>The sources the file was written from; just <see cref="Source"/> when the file holds only one module.</summary>
    public IReadOnlyList<OutputSource> AllSources =>
        Sources.Count > 0 ? Sources : [new OutputSource(Source, SourceSize, 0, LineBytes.Count)];

    /// <summary>A file with no line lengths worked out, such as one a test wrote by hand.</summary>
    public OutputFile(string path, string text) : this(path, text, []) { }
}
