using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
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
/// <param name="Configuration">Which <c>.if</c> branches this build takes.</param>
/// <param name="Diagnostics">Everything wrong with the program, ordered by file, line and column.</param>
public sealed record ProgramAnalysis(
    ProgramModel Program,
    Cpu Cpu,
    IReadOnlyList<FileAnalysis> Files,
    Configuration Configuration,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    // What requests have asked of each analysis, kept by instance. It cannot be a field, because
    // `with` copies fields, and a copy may have other diagnostics or other program-wide answers.
    private static readonly ConditionalWeakTable<ProgramAnalysis, Answers> answers = new();

    // The files by their logical paths. Where two files share a path, the first is found.
    private readonly Dictionary<string, FileAnalysis> byPath = ByPath(Files);

    // The constants some instruction of the program uses as an address, found the first time a
    // file's suggestions are asked for, because a constant is used across the program and declared
    // in one file.
    private readonly Lazy<IReadOnlySet<Symbol>> usedAsAddresses = new(() => Flow.AddressConstants.UsedAsAddresses(Files, Program.Current));

    // The code bytes some operand or data value of the program names other than as where control
    // goes, found the first time a file's suggestions are asked for, because the code that reads an
    // instruction's bytes may be in another file.
    private readonly Lazy<IReadOnlyList<Flow.Suggestions.NamedByte>> namedBytes = new(() => Flow.Suggestions.NamedBytes(Files, Program.Current));

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
    /// Gets a value indicating whether the program-wide answers are worked out for these files.
    /// Those answers are what each routine keeps, reads, costs with its calls, leaves on its
    /// caller's stack and returns with in the flags. An editor asks for an analysis after an edit
    /// without them, so that the edited file is answered at once, and <see cref="Compiler.Settle"/>
    /// works them out once typing stops. Until then, the files the edit did not reach keep the
    /// answers from before it, and the program-wide diagnostics are those from before, moved to
    /// follow the edit.
    /// </summary>
    public bool IsSettled { get; internal init; } = true;

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
    /// Gets the routines that depend on the depth of the stack they are entered with, which a tail
    /// call to them would change.
    /// </summary>
    internal IReadOnlySet<RoutineKey> CallerStackReaders { get; init; } = new HashSet<RoutineKey>();

    /// <summary>
    /// Gets what a later analysis of the same program, after one edit, needs in order to reuse the
    /// parts of this one that the edit did not affect.
    /// </summary>
    internal Reuse? Reused { get; init; }

    /// <summary>
    /// Returns the diagnostics for one file, for an editor that shows a file at a time. The
    /// diagnostics are sorted into files the first time any file's are asked for.
    /// </summary>
    public IReadOnlyList<Diagnostic> DiagnosticsFor(string path) =>
        Asked().ByFile.Value.GetValueOrDefault(path) ?? [];

    /// <summary>
    /// Returns the places in one file where the code could be smaller or faster, or where a constant
    /// used as an address could be declared as data, for an editor to suggest. No build reports them.
    /// Each file's are looked for once, the first time they are asked for.
    /// </summary>
    public IReadOnlyList<Diagnostic> SuggestionsFor(string path) =>
        Asked().Suggestions.GetOrAdd(path, path => FileFor(path) is { } file
            ? Norristown.Diagnostics.Ordered([
                .. Flow.Suggestions.For(file, Configuration.Omitted(file.Model.Tree), ReadsCallerStack, namedBytes.Value),
                .. Flow.AddressConstants.For(file, usedAsAddresses.Value)])
            : []);

    /// <summary>
    /// Returns where each routine of the program runs from: under an interrupt, in the rest of the
    /// program, or both. They are worked out once, the first time they are asked for.
    /// </summary>
    public RoutineContexts Contexts() => Asked().Contexts.Value;

    /// <summary>
    /// Returns whether a routine depends on the depth of the stack it was entered with, which a
    /// tail call to it would change.
    /// </summary>
    private bool ReadsCallerStack(Symbol routine) => CallerStackReaders.Contains(RoutineKey.Of(routine));

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

    /// <summary>Returns what requests have asked of this analysis so far.</summary>
    private Answers Asked() => answers.GetValue(this, analysis => new Answers(analysis));

    /// <summary>Holds what an analysis keeps so that the next analysis can start from it.</summary>
    /// <param name="Project">
    /// The project it analyzed. The next analysis can reuse this one only for the same project.
    /// </param>
    /// <param name="Trees">Every file analyzed.</param>
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
        IReadOnlyDictionary<string, long?> Lengths)
    {
        /// <summary>
        /// Gets the diagnostics found by working out the program-wide answers, which an analysis
        /// that leaves those answers for later carries over from the one before it.
        /// </summary>
        public IReadOnlyList<Diagnostic> Composed { get; init; } = [];
    }

    /// <summary>Holds what requests have asked of one analysis, so that it is worked out once.</summary>
    /// <param name="analysis">The analysis asked about.</param>
    private sealed class Answers(ProgramAnalysis analysis)
    {
        /// <summary>Gets each file's diagnostics, in the order the analysis has them, by path.</summary>
        public Lazy<Dictionary<string, List<Diagnostic>>> ByFile { get; } = new(() =>
        {
            var byFile = new Dictionary<string, List<Diagnostic>>(StringComparer.Ordinal);
            foreach (var diagnostic in analysis.Diagnostics)
            {
                if (diagnostic.Span.File is not { } file)
                    continue;
                if (!byFile.TryGetValue(file, out var found))
                    byFile[file] = found = [];
                found.Add(diagnostic);
            }
            return byFile;
        });

        /// <summary>Gets where each routine of the program runs from.</summary>
        public Lazy<RoutineContexts> Contexts { get; } = new(() => RoutineContexts.Of(analysis));

        /// <summary>Gets the suggestions found so far, by the path of their file.</summary>
        public ConcurrentDictionary<string, IReadOnlyList<Diagnostic>> Suggestions { get; } = new(StringComparer.Ordinal);
    }
}
