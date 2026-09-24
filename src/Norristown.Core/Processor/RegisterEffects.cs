using Norristown.Syntax;

namespace Norristown.Processor;

/// <summary>
/// Determines which registers each instruction writes, and which instructions copy one
/// register's value into another rather than producing a new value. The instruction tables give
/// the widest answer, and this class narrows it where the mode or the operand decides.
/// <para>
/// A write is any change the caller cannot predict, so a transfer counts as one even though the
/// new value came from another register. <see cref="Moved"/> reports where the value came from,
/// and <see cref="Written"/> reports only that the register was written.
/// </para>
/// </summary>
public static class RegisterEffects
{
    /// <summary>
    /// Returns the registers <paramref name="mnemonic"/> writes. <paramref name="constant"/> is
    /// the value of an immediate operand when it is known, and it decides whether a <c>rep</c> or
    /// a <c>sep</c> changes the carry.
    /// </summary>
    public static Registers Written(MnemonicKind mnemonic, AddressingMode? mode, long? constant) => mnemonic switch
    {
        // A shift through the accumulator writes it; one through memory writes only the carry.
        MnemonicKind.Asl or MnemonicKind.Lsr or MnemonicKind.Rol or MnemonicKind.Ror =>
            mode == AddressingMode.Accumulator ? Registers.A | Registers.C : Registers.C,
        MnemonicKind.Inc or MnemonicKind.Dec => mode == AddressingMode.Accumulator ? Registers.A : Registers.None,

        // `rep` and `sep` write the flags their operand names, and bit 0 is the carry. An
        // operand whose value nt65 cannot work out may name the carry, so it is assumed to.
        MnemonicKind.Rep or MnemonicKind.Sep => constant is { } flags && (flags & (long)StatusFlags.Carry) == 0 ? Registers.None : Registers.C,

        _ => Instructions.Facts(mnemonic).Writes,
    };

    /// <summary>
    /// Returns the register a transfer copies and the register it copies to, for the transfers
    /// among the three registers that hold values, or null for every other instruction.
    /// </summary>
    public static (Registers From, Registers To)? Moved(MnemonicKind mnemonic) => Instructions.Facts(mnemonic).Copies;

    /// <summary>
    /// Formats <paramref name="registers"/> as a message or a code lens names them, as the names
    /// <c>A</c>, <c>X</c>, <c>Y</c> and <c>C</c> separated by commas.
    /// </summary>
    public static string Format(Registers registers) =>
        string.Join(", ", Each(registers).Select(register => register switch
        {
            Registers.A => "A",
            Registers.X => "X",
            Registers.Y => "Y",
            _ => "C",
        }));

    /// <summary>Returns each register in <paramref name="registers"/>, one at a time, in a fixed order.</summary>
    public static IEnumerable<Registers> Each(Registers registers)
    {
        foreach (var register in new[] { Registers.A, Registers.X, Registers.Y, Registers.C })
        {
            if (registers.HasFlag(register))
                yield return register;
        }
    }

    /// <summary>
    /// Returns the register that a register name in a <c>keeps</c> item stands for, or null if
    /// <paramref name="name"/> is not a register name.
    /// </summary>
    public static Registers? Named(string name) => name.ToLowerInvariant() switch
    {
        "a" => Registers.A,
        "x" => Registers.X,
        "y" => Registers.Y,
        "c" => Registers.C,
        _ => null,
    };
}
