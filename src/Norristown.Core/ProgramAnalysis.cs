using Norristown.Flow;
using Norristown.Layout;
using Norristown.Processor;
using Norristown.Project;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown;

/// <summary>
/// Represents everything a program means, short of emitting it. That includes what its names
/// refer to, what its lines assemble to, and what is wrong with it. The compiler emits from this,
/// and the language server answers the editor from it, so the two never disagree about a program.
/// </summary>
/// <param name="Program">Every file's model, and what the files can see of one another.</param>
/// <param name="Cpu">The processor the program is built for.</param>
/// <param name="Files">
/// What analyzing each file of <see cref="Program"/> on its own found, in the same order.
/// </param>
/// <param name="Defines">The file the build configuration was read as, or null.</param>
/// <param name="Configuration">Which <c>.if</c> branches this build takes.</param>
/// <param name="Diagnostics">Everything wrong with the program, ordered by file, line and column.</param>
public sealed record ProgramAnalysis(
    ProgramModel Program,
    Cpu Cpu,
    IReadOnlyList<FileAnalysis> Files,
    SyntaxTree? Defines,
    Configuration Configuration,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    // The files by their logical paths. Where two files share a path, the first is found.
    private readonly Dictionary<string, FileAnalysis> byPath = ByPath(Files);

    /// <summary>
    /// Gets what analyzing each file of <see cref="Program"/> on its own found, in the same order.
    /// It cannot be replaced, so that the lookups by path always agree with it.
    /// </summary>
    public IReadOnlyList<FileAnalysis> Files { get; } = Files;

    /// <summary>
    /// Gets the number of files this analysis analyzed. That is every file of the program, or only
    /// the files that changed and the files the changes affect when the rest could be kept from
    /// the previous analysis.
    /// </summary>
    public int Reanalyzed { get; internal init; }

    /// <summary>
    /// Gets the reason every file was analyzed, or null when only the changed files were.
    /// </summary>
    public WholeProgramReason? WholeProgram { get; internal init; }

    /// <summary>
    /// Gets which modules place which other modules, and so which translation units the program
    /// is emitted as.
    /// </summary>
    public Placements Placements { get; internal init; } = Placements.None;

    /// <summary>
    /// Gets the logical paths of the files whose lengths <c>.incbin</c> directives were measured
    /// from. An editor that sees one of them change on disk analyzes the program again.
    /// </summary>
    public IEnumerable<string> Binaries => Reused?.Lengths.Keys ?? [];

    /// <summary>
    /// Gets what a later analysis of the same program, after one edit, needs in order to reuse the
    /// parts of this one that the edit did not affect.
    /// </summary>
    internal Reuse? Reused { get; init; }

    /// <summary>
    /// Returns the diagnostics for one file, for an editor that shows a file at a time.
    /// </summary>
    public IReadOnlyList<Diagnostic> DiagnosticsFor(string path) =>
        [.. Diagnostics.Where(diagnostic => diagnostic.Span.File == path)];

    /// <summary>
    /// Returns what analyzing <paramref name="path"/> on its own found, or null when the program
    /// has no such file.
    /// </summary>
    public FileAnalysis? FileFor(string path) => byPath.GetValueOrDefault(path);

    /// <summary>
    /// Returns the model for <paramref name="path"/>, or null when the program has no such file.
    /// </summary>
    public SemanticModel? ModelFor(string path) => FileFor(path)?.Model;

    /// <summary>
    /// Returns the layout of <paramref name="path"/>, which says what its lines assemble to, or
    /// null when the program has no such file.
    /// </summary>
    public CodeLayout? LayoutFor(string path) => FileFor(path)?.Layout;

    /// <summary>
    /// Returns the control flow of <paramref name="path"/>, or null when the program has no such
    /// file.
    /// </summary>
    public ControlFlow? FlowFor(string path) => FileFor(path)?.Flow;

    /// <summary>
    /// Returns the processor state through <paramref name="path"/>, or null when there is none to
    /// track.
    /// </summary>
    public StateAnalysis? StatesFor(string path) => FileFor(path)?.State;

    /// <summary>Returns <paramref name="files"/> by their logical paths, keeping the first of each.</summary>
    private static Dictionary<string, FileAnalysis> ByPath(IReadOnlyList<FileAnalysis> files)
    {
        var byPath = new Dictionary<string, FileAnalysis>(StringComparer.Ordinal);
        foreach (var file in files)
            byPath.TryAdd(file.Path, file);
        return byPath;
    }

    /// <summary>Holds what an analysis keeps so that the next analysis can start from it.</summary>
    /// <param name="Project">
    /// The project it analyzed. The next analysis can reuse this one only for the same project.
    /// </param>
    /// <param name="Trees">Every file analyzed, including the defines file.</param>
    /// <param name="Conditions">The diagnostics from evaluating each file's conditions, by file.</param>
    /// <param name="Analyzed">
    /// The diagnostics from laying out each file and following its control flow, by file.
    /// </param>
    /// <param name="SegmentTable">The diagnostics from building the segment table.</param>
    /// <param name="Lengths">The length taken for each <c>.incbin</c> file.</param>
    internal sealed record Reuse(
        ProjectSettings Project,
        IReadOnlyList<SyntaxTree> Trees,
        IReadOnlyDictionary<string, IReadOnlyList<Diagnostic>> Conditions,
        IReadOnlyDictionary<string, IReadOnlyList<Diagnostic>> Analyzed,
        IReadOnlyList<Diagnostic> SegmentTable,
        IReadOnlyDictionary<string, long?> Lengths);
}
