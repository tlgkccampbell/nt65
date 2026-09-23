namespace Norristown.Semantics;

/// <summary>
/// What the analysis knows of the 65816 at one point: the widths and the mode, which decide
/// how an immediate is sized, and the direct page and the data bank, which decide what memory
/// a direct or an absolute operand reaches.
/// </summary>
/// <param name="A">How wide the accumulator is.</param>
/// <param name="Index">How wide X and Y are.</param>
/// <param name="E">Whether the processor is in emulation mode.</param>
/// <param name="D">The direct page.</param>
/// <param name="B">The data bank.</param>
public readonly record struct ProcessorState(
    Width A, Width Index, ProcessorMode E, StateValue D = default, StateValue B = default)
{
    /// <summary>
    /// What a signature that says nothing declares: <c>a*, i*, native, dp*, dbr*</c>. The widths
    /// are unchanged rather than 8 because assuming a width would claim something the author
    /// never said: a body that depends on one must say which, and one that does not can be
    /// called whatever the caller's widths are.
    /// </summary>
    public static ProcessorState Default => new(Width.Unchanged, Width.Unchanged, ProcessorMode.Native);

    /// <summary>Nothing known about any part.</summary>
    public static ProcessorState Unknown => new(
        Width.Unknown, Width.Unknown, ProcessorMode.Unknown, StateValue.Unknown, StateValue.Unknown);

    /// <summary>The width of <paramref name="register"/>.</summary>
    public Width Of(Processor.WidthRegister register) => register == Processor.WidthRegister.A ? A : Index;

    /// <summary>
    /// The state as a signature writes it: <c>a16, i8, native</c>, with the direct page and the
    /// data bank where they are anything other than unchanged.
    /// </summary>
    public override string ToString() =>
        $"{Spell("a", A)}, {Spell("i", Index)}, {Spell(E)}"
        + (D.Kind == StateValueKind.Unchanged ? "" : ", " + D.Spell("dp"))
        + (B.Kind == StateValueKind.Unchanged ? "" : ", " + B.Spell("dbr"));

    /// <summary>One width as an item: <c>a8</c>, <c>a16</c>, <c>a?</c> or <c>a*</c>.</summary>
    public static string Spell(string register, Width width) => width switch
    {
        Width.Eight => register + "8",
        Width.Sixteen => register + "16",
        Width.Unknown => register + "?",
        _ => register + "*",
    };

    /// <summary>The mode as an item: <c>native</c>, <c>emu</c>, <c>e?</c> or <c>e*</c>.</summary>
    public static string Spell(ProcessorMode mode) => mode switch
    {
        ProcessorMode.Native => "native",
        ProcessorMode.Emulation => "emu",
        ProcessorMode.Unknown => "e?",
        _ => "e*",
    };
}
