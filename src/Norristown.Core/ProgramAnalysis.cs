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
/// One layout per file of <see cref="Program"/>, in the same order, or empty for a CPU whose
/// instructions nt65 cannot size yet.
/// </param>
/// <param name="Defines">The file the build configuration was read as, or null.</param>
/// <param name="Configuration">Which <c>.if</c> branches this build takes.</param>
/// <param name="Diagnostics">Everything wrong with the program, ordered by file, line and column.</param>
public sealed record ProgramAnalysis(
    ProgramModel Program,
    Cpu Cpu,
    IReadOnlyList<CodeLayout> Layouts,
    SyntaxTree? Defines,
    Configuration Configuration,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>What is wrong with one file, for an editor that shows a file at a time.</summary>
    public IReadOnlyList<Diagnostic> DiagnosticsFor(string path) =>
        [.. Diagnostics.Where(diagnostic => diagnostic.Span.File == path)];

    /// <summary>The model for <paramref name="path"/>, or null when the program has no such file.</summary>
    public SemanticModel? ModelFor(string path) =>
        Program.Files.FirstOrDefault(file => file.Tree.Path == path);
}
