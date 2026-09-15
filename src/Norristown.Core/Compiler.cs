namespace Norristown;

public sealed record Compilation(IReadOnlyList<OutputFile> Outputs, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// The whole pipeline, from a set of source files to ca65 output and diagnostics. The
/// order of <c>files</c> must not affect the result.
/// </summary>
public static class Compiler
{
    // Stage 0: no layers yet, so every program compiles to nothing.
    public static Compilation Compile(IReadOnlyCollection<SourceFile> files) => new([], []);
}
