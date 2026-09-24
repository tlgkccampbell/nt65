namespace Norristown;

/// <summary>
/// Represents one generated ca65 file. <see cref="Text"/> always uses <c>\n</c> line endings.
/// </summary>
/// <param name="Path">The path the file is written to, relative to the project root.</param>
/// <param name="Text">The ca65 source.</param>
/// <param name="LineBytes">
/// How many bytes nt65 expects each line of <see cref="Text"/> to assemble to, one
/// entry per line, or -1 for a line whose length only the assembler determines, such as an
/// <c>.align</c>. These are the lengths the analysis works from, so comparing them with
/// what ca65 actually generates shows whether nt65 and ca65 agree.
/// </param>
public sealed record OutputFile(string Path, string Text, IReadOnlyList<int> LineBytes)
{
    /// <summary>
    /// Gets the files this output depends on, as sorted logical paths. They are the sources whose
    /// changes may change it, and the binaries it includes.
    /// </summary>
    public IReadOnlyList<string> Dependencies { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether the file holds nothing but its header. That is the case for
    /// the ca65 of a module that declares only charmaps, functions, lists, macros and the like,
    /// which cross modules by value. A build writes no file for it, since there would be nothing
    /// in it to assemble or link.
    /// </summary>
    public bool IsEmpty { get; init; }

    /// <summary>Gets the kind of file this is, either the ca65 of a module or the line map beside it.</summary>
    public OutputKind Kind { get; init; } = OutputKind.Ca65;

    /// <summary>Gets the logical path of the source this file was generated from.</summary>
    public string Source { get; init; } = "";

    /// <summary>
    /// Gets the size of that source in bytes. The line map records it so that ld65's debug file
    /// can record it too, and so that a debugger can tell that the source has changed under it.
    /// </summary>
    public int SourceSize { get; init; }

    /// <summary>
    /// Gets the line of <see cref="Source"/> that each line of <see cref="Text"/> came from, one
    /// entry per line, or 0 for a line with no source line a debugger should point to.
    /// <see cref="Emit.LineMap"/> writes these beside the output, in place of the
    /// <c>.dbg line</c> directives that would otherwise have to sit between every two lines of ca65.
    /// </summary>
    public IReadOnlyList<int> LineSources { get; init; } = [];

    /// <summary>
    /// Gets, for each line, the index into <see cref="Sources"/> of the file that its entry in
    /// <see cref="LineSources"/> refers to. It is empty when every line came from
    /// <see cref="Source"/>.
    /// </summary>
    public IReadOnlyList<int> LineFiles { get; init; } = [];

    /// <summary>
    /// Gets every source the file was generated from, with <see cref="Source"/> first and then
    /// each module placed in it, in the order they are emitted. It is empty when the file holds
    /// only one module.
    /// </summary>
    public IReadOnlyList<OutputSource> Sources { get; init; } = [];

    /// <summary>
    /// Gets the sources the file was generated from, which is just <see cref="Source"/> when the
    /// file holds only one module.
    /// </summary>
    public IReadOnlyList<OutputSource> AllSources =>
        Sources.Count > 0 ? Sources : [new OutputSource(Source, SourceSize, 0, LineBytes.Count)];

    /// <summary>
    /// Initializes a file with no line lengths worked out, such as one a test wrote by hand.
    /// </summary>
    public OutputFile(string path, string text) : this(path, text, []) { }
}
