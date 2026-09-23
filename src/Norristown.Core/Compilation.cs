namespace Norristown;

/// <summary>The result of compiling a program: the files to write, and everything to report.</summary>
/// <param name="Outputs">
/// One ca65 file per input file, each followed by the line map beside it, for a file that has
/// one (<see cref="Emit.LineMap"/>).
/// </param>
/// <param name="Diagnostics">Errors, warnings and information, ordered by file, line and column.</param>
public sealed record Compilation(IReadOnlyList<OutputFile> Outputs, IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>The ca65 source files among the outputs: the files that make up the program and are assembled.</summary>
    public IEnumerable<OutputFile> Ca65 => Outputs.Where(output => output.Kind == OutputKind.Ca65);

    /// <summary>The C header of what the program exports, when one was asked for and the program has no errors.</summary>
    public string? Header { get; init; }

    /// <summary>
    /// Whether neither the project nor a <c>.cpu</c> item said which processor the program is
    /// for, so that it was built for <see cref="Processor.ProgramCpu.Default"/>.
    /// </summary>
    public bool IsCpuAssumed { get; init; }
}
