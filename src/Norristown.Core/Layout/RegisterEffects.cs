namespace Norristown.Layout;

/// <summary>
/// Which registers each instruction writes, and which ones move a register's value to another
/// register rather than making one up. It is the same table on every CPU: an instruction one
/// CPU lacks is never asked about, and no CPU here spells one instruction two ways.
/// <para>
/// A write is any change the caller cannot predict, so a transfer counts as one even though
/// what lands in the register came from another: what the value is worth is
/// <see cref="Moved"/>'s answer, and this one only says the register was written at all.
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
        "lda" or "pla" or "txa" or "tya" or "tdc" or "tsc" or "xba" or "and" or "ora" or "eor" => Registers.A,
        "adc" or "sbc" => Registers.A | Registers.C,
        "ldx" or "plx" or "tax" or "tsx" or "tyx" or "inx" or "dex" => Registers.X,
        "ldy" or "ply" or "tay" or "txy" or "iny" or "dey" => Registers.Y,
        "cmp" or "cpx" or "cpy" or "clc" or "sec" or "plp" or "rti" => Registers.C,

        // A shift through the accumulator writes it; one through memory writes only the carry.
        "asl" or "lsr" or "rol" or "ror" =>
            mode == AddressingMode.Accumulator ? Registers.A | Registers.C : Registers.C,
        "inc" or "dec" => mode == AddressingMode.Accumulator ? Registers.A : Registers.None,

        // A block move counts down in A and walks X and Y along the two banks.
        "mvn" or "mvp" => Registers.A | Registers.X | Registers.Y,

        // Swapping the carry with the emulation flag changes the mode, which truncates the
        // index registers and hides half the accumulator, so nothing survives it.
        "xce" => Registers.All,

        // `rep` and `sep` write the flags their operand names, and bit 0 is the carry. An
        // operand nt65 cannot work out may name it.
        "rep" or "sep" => constant is { } flags && (flags & 1) == 0 ? Registers.None : Registers.C,

        // A software interrupt runs a handler this program may not even hold, so what it
        // leaves is nothing anyone can say. The analysis takes it as a call it cannot follow,
        // and asks this only for what it would write if it were an instruction like any other.
        "brk" or "cop" => Registers.All,

        _ => Registers.None,
    };

    /// <summary>
    /// The register a transfer copies, and the one it copies to, for the transfers between the
    /// three registers a value is held in; null for every other instruction. The stack pointer
    /// and the 65816's D are not among them, so <c>tsx</c> and <c>tdc</c> are plain writes.
    /// </summary>
    public static (Registers From, Registers To)? Moved(string mnemonic) => mnemonic switch
    {
        "tax" => (Registers.A, Registers.X),
        "tay" => (Registers.A, Registers.Y),
        "txa" => (Registers.X, Registers.A),
        "tya" => (Registers.Y, Registers.A),
        "txy" => (Registers.X, Registers.Y),
        "tyx" => (Registers.Y, Registers.X),
        _ => null,
    };

    /// <summary>The register as a message and a lens name it: <c>A</c>, <c>X</c>, <c>Y</c>, <c>C</c>.</summary>
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
