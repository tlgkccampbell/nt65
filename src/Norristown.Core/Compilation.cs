namespace Norristown;

/// <summary>The result of compiling a program: the files to write, and everything to report.</summary>
/// <param name="Outputs">
/// One ca65 file per input file, each followed by the line map beside it, for a file that has
/// one (<see cref="Emit.LineMap"/>).
/// </param>
/// <param name="Diagnostics">Errors, warnings and information, ordered by file, line and column.</param>
public sealed record Compilation(IReadOnlyList<OutputFile> Outputs, IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>The ca65 among the outputs: the files that are the program, and are assembled.</summary>
    public IEnumerable<OutputFile> Ca65 => Outputs.Where(output => output.Kind == OutputKind.Ca65);

    /// <summary>The C header of what the program exports, when one was asked for and the program is not wrong.</summary>
    public string? Header { get; init; }

    /// <summary>
    /// Whether nothing said which processor the program is for, neither the project nor a
    /// <c>.cpu</c> item, so that it was built for <see cref="Project.ProgramCpu.Default"/>.
    /// </summary>
    public bool IsCpuAssumed { get; init; }
}
