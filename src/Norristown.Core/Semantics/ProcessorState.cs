namespace Norristown.Semantics;

/// <summary>
/// Represents what the analysis knows of the 65816 at one point. The register widths and the
/// mode decide how an immediate is sized. The direct page and the data bank decide what memory a
/// direct or an absolute operand reaches.
/// </summary>
/// <param name="A">The width of the accumulator.</param>
/// <param name="Index">The width of X and Y.</param>
/// <param name="E">Whether the processor is in emulation mode.</param>
/// <param name="D">The direct page.</param>
/// <param name="B">The data bank.</param>
public readonly record struct ProcessorState(
    Width A, Width Index, ProcessorMode E, StateValue D = default, StateValue B = default)
{
    /// <summary>
    /// Gets the state a signature declares when it states nothing, which is
    /// <c>a*, i*, native, dp*, dbr*</c>. The widths are unchanged rather than 8, because assuming
    /// a width would claim something the author never stated. A body that depends on a width must
    /// state it, and a body that does not can be called with any widths.
    /// </summary>
    public static ProcessorState Default => new(Width.Unchanged, Width.Unchanged, ProcessorMode.Native);

    /// <summary>Gets a state in which nothing is known about any part.</summary>
    public static ProcessorState Unknown => new(
        Width.Unknown, Width.Unknown, ProcessorMode.Unknown, StateValue.Unknown, StateValue.Unknown);

    /// <summary>Returns the width of <paramref name="register"/>.</summary>
    public Width Of(Processor.WidthRegister register) => register == Processor.WidthRegister.A ? A : Index;

    /// <summary>
    /// Returns the state formatted as in a signature, such as <c>a16, i8, native</c>. The direct
    /// page and the data bank are included when they are anything other than unchanged.
    /// </summary>
    public override string ToString() =>
        $"{Format("a", A)}, {Format("i", Index)}, {Format(E)}"
        + (D.Kind == StateValueKind.Unchanged ? "" : ", " + D.Format("dp"))
        + (B.Kind == StateValueKind.Unchanged ? "" : ", " + B.Format("dbr"));

    /// <summary>
    /// Formats one width as a signature item: <c>a8</c>, <c>a16</c>, <c>a?</c> or <c>a*</c>.
    /// </summary>
    public static string Format(string register, Width width) => width switch
    {
        Width.Eight => register + "8",
        Width.Sixteen => register + "16",
        Width.Unknown => register + "?",
        _ => register + "*",
    };

    /// <summary>
    /// Formats the mode as a signature item: <c>native</c>, <c>emu</c>, <c>e?</c> or <c>e*</c>.
    /// </summary>
    public static string Format(ProcessorMode mode) => mode switch
    {
        ProcessorMode.Native => "native",
        ProcessorMode.Emulation => "emu",
        ProcessorMode.Unknown => "e?",
        _ => "e*",
    };
}
