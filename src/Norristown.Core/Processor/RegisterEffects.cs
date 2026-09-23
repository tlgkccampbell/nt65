namespace Norristown.Processor;

/// <summary>
/// Which registers each instruction writes, and which instructions copy one register's value
/// into another rather than producing a new value. The instruction tables give the widest
/// answer; this narrows it where the mode or the operand decides.
/// <para>
/// A write is any change the caller cannot predict, so a transfer counts as one even though
/// the new value came from another register: where the value came from is what
/// <see cref="Moved"/> says, and <see cref="Written"/> only says the register was written at all.
/// </para>
/// </summary>
public static class RegisterEffects
{
    /// <summary>
    /// The registers <paramref name="mnemonic"/> writes. <paramref name="constant"/> is the
    /// value of an immediate operand where it is known, which is what says whether a
    /// <c>rep</c> or a <c>sep</c> touches the carry.
    /// </summary>
    public static Registers Written(string mnemonic, AddressingMode? mode, long? constant) => mnemonic switch
    {
        // A shift through the accumulator writes it; one through memory writes only the carry.
        "asl" or "lsr" or "rol" or "ror" =>
            mode == AddressingMode.Accumulator ? Registers.A | Registers.C : Registers.C,
        "inc" or "dec" => mode == AddressingMode.Accumulator ? Registers.A : Registers.None,

        // `rep` and `sep` write the flags their operand names, and bit 0 is the carry. An
        // operand whose value nt65 cannot work out may name the carry, so it is assumed to.
        "rep" or "sep" => constant is { } flags && (flags & 1) == 0 ? Registers.None : Registers.C,

        _ => Instructions.Facts(mnemonic).Writes,
    };

    /// <summary>
    /// The register a transfer copies, and the one it copies to, for the transfers between the
    /// three registers a value is held in; null for every other instruction.
    /// </summary>
    public static (Registers From, Registers To)? Moved(string mnemonic) => Instructions.Facts(mnemonic).Copies;

    /// <summary>
    /// The registers as a message or a code lens names them, separated by commas: <c>A</c>,
    /// <c>X</c>, <c>Y</c>, <c>C</c>.
    /// </summary>
    public static string Spell(Registers registers) =>
        string.Join(", ", Each(registers).Select(register => register switch
        {
            Registers.A => "A",
            Registers.X => "X",
            Registers.Y => "Y",
            _ => "C",
        }));

    /// <summary>The registers of <paramref name="registers"/>, one at a time, in a fixed order.</summary>
    public static IEnumerable<Registers> Each(Registers registers)
    {
        foreach (var register in new[] { Registers.A, Registers.X, Registers.Y, Registers.C })
        {
            if (registers.HasFlag(register))
                yield return register;
        }
    }

    /// <summary>The register a <c>keeps</c> item's register name stands for, or null when it is not one.</summary>
    public static Registers? Named(string name) => name.ToLowerInvariant() switch
    {
        "a" => Registers.A,
        "x" => Registers.X,
        "y" => Registers.Y,
        "c" => Registers.C,
        _ => null,
    };
}
