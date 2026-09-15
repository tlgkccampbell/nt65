namespace Norristown;

/// <summary>The result of compiling a program: the files to write, and everything to report.</summary>
/// <param name="Outputs">One ca65 file per input file.</param>
/// <param name="Diagnostics">Errors, warnings and information, ordered by file, line and column.</param>
public sealed record Compilation(IReadOnlyList<OutputFile> Outputs, IReadOnlyList<Diagnostic> Diagnostics);
