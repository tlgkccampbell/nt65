namespace Norristown.Processor;

/// <summary>
/// Describes what a mnemonic does, beyond which addressing modes it has. The facts cover how
/// it affects control flow, what it moves on and off the stack, whether it writes the memory
/// its operand names, and which registers it leaves changed. Every pass after layout needs
/// some of these facts; if each pass tested the lowercase mnemonic itself, two passes could
/// come to disagree.
/// <para>
/// The table is the same on every CPU, because an instruction one CPU lacks is never asked
/// about, and no CPU here has two names for one instruction. Facts that depend on the operand
/// or the mode, such as a shift through the accumulator or the flags a <c>rep</c> names, are
/// left to the code that knows the operand or mode. That code starts from the widest answer
/// given here.
/// </para>
/// </summary>
public sealed record InstructionFacts
{
    /// <summary>Gets the facts for a mnemonic with none of these properties.</summary>
    public static InstructionFacts None { get; } = new();

    /// <summary>
    /// Gets how the mnemonic affects control flow: whether it branches, jumps, calls, returns or
    /// stops.
    /// </summary>
    public Control Control { get; init; }

    /// <summary>
    /// Gets a value indicating whether the mnemonic writes the memory its operand names,
    /// including by a read-modify-write.
    /// </summary>
    public bool Stores { get; init; }

    /// <summary>Gets how much of the stack one of its pushes takes, or null if it pushes nothing.</summary>
    public PushSize? Pushes { get; init; }

    /// <summary>Gets how much of the stack one of its pulls takes, or null if it pulls nothing.</summary>
    public PushSize? Pulls { get; init; }

    /// <summary>
    /// Gets the register its push saves or its pull fills, or <see cref="Registers.None"/> if
    /// what it moves is not one of those registers, as with the data bank, the direct page and
    /// the program bank.
    /// </summary>
    public Registers Held { get; init; }

    /// <summary>
    /// Gets the registers the mnemonic may leave changed, for any operand and mode. A write is
    /// any change the caller cannot predict, so a transfer counts as one even though the value
    /// it puts in the register came from another register.
    /// </summary>
    public Registers Writes { get; init; }

    /// <summary>
    /// Gets the register the mnemonic copies and the register it copies to, for the transfers
    /// among the three registers that hold values, or null for every other mnemonic. The stack
    /// pointer and the 65816's D are not among the three, so <c>tsx</c> and <c>tdc</c> are plain
    /// writes.
    /// </summary>
    public (Registers From, Registers To)? Copies { get; init; }

    /// <summary>
    /// Gets the register whose width sizes the mnemonic's immediate on the 65816, or null if
    /// its immediate is always one byte. ca65 sizes exactly these immediates from its
    /// <c>.a8</c>/<c>.a16</c> and <c>.i8</c>/<c>.i16</c> settings.
    /// </summary>
    public WidthRegister? SizedBy { get; init; }
}
