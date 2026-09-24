namespace Norristown;

/// <summary>
/// Represents the result of compiling a program, which holds the files to write and the
/// diagnostics to report.
/// </summary>
/// <param name="Outputs">
/// One ca65 file per input file that produces any output (<see cref="OutputFile.IsEmpty"/>), each
/// followed by the line map beside it, for a file that has one (<see cref="Emit.LineMap"/>).
/// </param>
/// <param name="Diagnostics">Errors, warnings and information, ordered by file, line and column.</param>
public sealed record Compilation(IReadOnlyList<OutputFile> Outputs, IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>
    /// Gets the ca65 source files among the outputs, which are the files that make up the program
    /// and are assembled.
    /// </summary>
    public IEnumerable<OutputFile> Ca65 => Outputs.Where(output => output.Kind == OutputKind.Ca65);

    /// <summary>
    /// Gets the C header of what the program exports, when one was requested and the program has
    /// no errors.
    /// </summary>
    public string? Header { get; init; }

    /// <summary>
    /// Gets a value indicating whether neither the project nor a <c>.cpu</c> directive named the
    /// processor the program is for, so that it was built for
    /// <see cref="Processor.ProgramCpu.Default"/>.
    /// </summary>
    public bool IsCpuAssumed { get; init; }
}
