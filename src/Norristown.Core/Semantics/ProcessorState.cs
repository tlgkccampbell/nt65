namespace Norristown.Semantics;

/// <summary>
/// The widths and the mode of the 65816 at one point: the part of its state that decides how
/// an immediate is sized. The direct page and the data bank join it with the stage that
/// checks them.
/// </summary>
/// <param name="A">How wide the accumulator is.</param>
/// <param name="Index">How wide X and Y are.</param>
/// <param name="E">Whether the processor is in emulation mode.</param>
public readonly record struct ProcessorState(Width A, Width Index, ProcessorMode E)
{
    /// <summary>What a signature that says nothing declares: <c>a8, i8, native</c>.</summary>
    public static ProcessorState Default => new(Width.Eight, Width.Eight, ProcessorMode.Native);

    /// <summary>Nothing known about any part.</summary>
    public static ProcessorState Unknown => new(Width.Unknown, Width.Unknown, ProcessorMode.Unknown);

    /// <summary>The width of <paramref name="register"/>.</summary>
    public Width Of(Layout.WidthRegister register) => register == Layout.WidthRegister.A ? A : Index;

    /// <summary>The state as a signature writes it: <c>a16, i8, native</c>.</summary>
    public override string ToString() => $"{Spell("a", A)}, {Spell("i", Index)}, {Spell(E)}";

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
