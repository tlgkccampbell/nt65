namespace Norristown.Processor;

/// <summary>
/// What a mnemonic is, beyond which addressing modes it has: what it does to the path,
/// what it moves on and off the stack, whether it writes the memory its operand names, and
/// which registers it leaves changed. Every pass after layout needs some of this, and having
/// each pass test the lowercase mnemonic itself is how two passes come to disagree.
/// <para>
/// It is the same table on every CPU: an instruction one CPU lacks is never asked about, and
/// no CPU here spells one instruction two ways. What depends on the operand or the mode — a
/// shift through the accumulator, the flags a <c>rep</c> names — is left to the code that
/// knows the operand or mode, which takes the widest answer from here.
/// </para>
/// </summary>
public sealed record InstructionFacts
{
    /// <summary>The facts for a mnemonic with none of these properties.</summary>
    public static InstructionFacts None { get; } = new();

    /// <summary>What it does to the path running through it: branch, jump, call, return or stop.</summary>
    public Control Control { get; init; }

    /// <summary>Whether it writes the memory its operand names, read-modify-write among them.</summary>
    public bool Stores { get; init; }

    /// <summary>How much of the stack one of its pushes takes, or null when it pushes nothing.</summary>
    public PushSize? Pushes { get; init; }

    /// <summary>The same for a pull, or null when it pulls nothing.</summary>
    public PushSize? Pulls { get; init; }

    /// <summary>
    /// The register its push holds or its pull fills, or <see cref="Registers.None"/> where
    /// what it moves is not one of them: the data bank, the direct page, the program bank.
    /// </summary>
    public Registers Held { get; init; }

    /// <summary>
    /// The registers it may leave changed, whatever its operand and mode. A write is any
    /// change the caller cannot predict, so a transfer counts as one even though what lands in
    /// the register came from another.
    /// </summary>
    public Registers Writes { get; init; }

    /// <summary>
    /// The register it copies and the one it copies to, for the transfers between the three
    /// registers a value is held in; null for everything else. The stack pointer and the
    /// 65816's D are not among them, so <c>tsx</c> and <c>tdc</c> are plain writes.
    /// </summary>
    public (Registers From, Registers To)? Copies { get; init; }

    /// <summary>
    /// The register whose width sizes its immediate on the 65816, or null when its immediate
    /// is always one byte. ca65 sizes exactly these from its <c>.a8</c>/<c>.a16</c> and
    /// <c>.i8</c>/<c>.i16</c> settings.
    /// </summary>
    public WidthRegister? SizedBy { get; init; }
}
