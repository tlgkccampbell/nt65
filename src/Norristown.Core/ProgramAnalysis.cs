using Norristown.Flow;
using Norristown.Layout;
using Norristown.Project;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown;

/// <summary>
/// Everything a program means, short of writing it out: what its names refer to, what its
/// lines assemble to, and what is wrong with it. The compiler emits from this, and the
/// language server answers the editor from it, so the two never disagree about a program.
/// </summary>
/// <param name="Program">Every file's model, and what they can see of one another.</param>
/// <param name="Cpu">The processor the program is built for.</param>
/// <param name="Layouts">
/// One layout per file of <see cref="Program"/>, in the same order.
/// </param>
/// <param name="Flows">
/// Where control goes in each file of <see cref="Program"/>, in the same order.
/// </param>
/// <param name="States">
/// The processor state through each file of <see cref="Program"/>, in the same order, on the
/// 65816; empty on the processors that have no state to track.
/// </param>
/// <param name="Defines">The file the build configuration was read as, or null.</param>
/// <param name="Configuration">Which <c>.if</c> branches this build takes.</param>
/// <param name="Diagnostics">Everything wrong with the program, ordered by file, line and column.</param>
public sealed record ProgramAnalysis(
    ProgramModel Program,
    Cpu Cpu,
    IReadOnlyList<CodeLayout> Layouts,
    IReadOnlyList<ControlFlow> Flows,
    IReadOnlyList<StateAnalysis> States,
    SyntaxTree? Defines,
    Configuration Configuration,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>
    /// How many files this analysis analyzed: every file of the program, or only the one that
    /// changed when the rest could be kept from the analysis before.
    /// </summary>
    public int Reanalyzed { get; internal init; }

    /// <summary>Why every file was analyzed, or null when only the file that changed was.</summary>
    public WholeProgramReason? WholeProgram { get; internal init; }

    /// <summary>What a later analysis of the same program, one edit on, needs to keep the rest of this one.</summary>
    internal Reuse? Reused { get; init; }

    /// <summary>What is wrong with one file, for an editor that shows a file at a time.</summary>
    public IReadOnlyList<Diagnostic> DiagnosticsFor(string path) =>
        [.. Diagnostics.Where(diagnostic => diagnostic.Span.File == path)];

    /// <summary>The model for <paramref name="path"/>, or null when the program has no such file.</summary>
    public SemanticModel? ModelFor(string path) =>
        Program.Files.FirstOrDefault(file => file.Tree.Path == path);

    /// <summary>What <paramref name="path"/>'s lines assemble to, or null when it was not laid out.</summary>
    public CodeLayout? LayoutFor(string path) => At(Layouts, path);

    /// <summary>Where control goes in <paramref name="path"/>, or null when it was not laid out.</summary>
    public ControlFlow? FlowFor(string path) => At(Flows, path);

    /// <summary>The processor state through <paramref name="path"/>, or null when there is none to track.</summary>
    public StateAnalysis? StatesFor(string path) => At(States, path);

    /// <summary>
    /// The entry of <paramref name="alongside"/> for <paramref name="path"/>. Every list
    /// here runs in the same order as <see cref="ProgramModel.Files"/>, or is empty.
    /// </summary>
    private T? At<T>(IReadOnlyList<T> alongside, string path)
        where T : class
    {
        for (var i = 0; i < Program.Files.Count && i < alongside.Count; i++)
        {
            if (Program.Files[i].Tree.Path == path)
                return alongside[i];
        }
        return null;
    }

    /// <summary>What an analysis keeps so that the next one can start from it.</summary>
    /// <param name="Project">The project it analyzed, which the next one has to be of too.</param>
    /// <param name="Trees">Every file analyzed, the defines among them.</param>
    /// <param name="Conditions">What answering each file's conditions found, by file.</param>
    /// <param name="Analyzed">What laying out each file and following its control found, by file.</param>
    /// <param name="SegmentTable">What building the segment table found.</param>
    /// <param name="Lengths">How long each <c>.incbin</c> file was taken to be.</param>
    internal sealed record Reuse(
        ProjectSettings Project,
        IReadOnlyList<SyntaxTree> Trees,
        IReadOnlyDictionary<string, IReadOnlyList<Diagnostic>> Conditions,
        IReadOnlyDictionary<string, IReadOnlyList<Diagnostic>> Analyzed,
        IReadOnlyList<Diagnostic> SegmentTable,
        IReadOnlyDictionary<string, long?> Lengths);
}
