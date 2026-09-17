namespace Norristown;

/// <summary>The result of compiling a program: the files to write, and everything to report.</summary>
/// <param name="Outputs">One ca65 file per input file.</param>
/// <param name="Diagnostics">Errors, warnings and information, ordered by file, line and column.</param>
public sealed record Compilation(IReadOnlyList<OutputFile> Outputs, IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>The C header of what the program exports, when one was asked for and the program is not wrong.</summary>
    public string? Header { get; init; }

    /// <summary>
    /// Whether nothing said which processor the program is for, neither the project nor a
    /// <c>.cpu</c> item, so that it was built for <see cref="Project.ProgramCpu.Default"/>.
    /// </summary>
    public bool IsCpuAssumed { get; init; }
}
