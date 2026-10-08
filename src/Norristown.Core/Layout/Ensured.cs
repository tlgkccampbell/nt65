using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Layout;

/// <summary>
/// Represents the instructions an <c>.ensure</c> emits. A <c>rep</c> makes the widths it names
/// 16 bits and a <c>sep</c> makes them 8 bits, and each is emitted only where the analysis did
/// not find that width already in effect. A flag the directive names is set by its own
/// instruction, such as <c>clc</c>, only where the flag analysis did not find it already so, or
/// by a <c>rep</c> or <c>sep</c> emitted for the widths anyway. The directive's effect on the
/// processor state does not depend on which instructions it emits, so the choice is made once the
/// analyses have reached a fixed point and never feeds back into them.
/// </summary>
/// <param name="Reset">
/// The flags the <c>rep</c> clears, or <see cref="StatusFlags.None"/> when there is no <c>rep</c>.
/// </param>
/// <param name="Set">
/// The flags the <c>sep</c> sets, or <see cref="StatusFlags.None"/> when there is no <c>sep</c>.
/// </param>
public readonly record struct Ensured(StatusFlags Reset, StatusFlags Set)
{
    /// <summary>Gets the flags cleared by an instruction of their own, such as <c>clc</c>.</summary>
    public StatusFlags Cleared { get; init; }

    /// <summary>Gets the flags set by an instruction of their own, such as <c>sec</c>.</summary>
    public StatusFlags Raised { get; init; }

    /// <summary>
    /// Gets the instructions that set the flags one at a time, in the order they are emitted.
    /// </summary>
    public IEnumerable<MnemonicKind> Instructions
    {
        get
        {
            var (cleared, raised) = (Cleared, Raised);
            return FlagValues.Named.SelectMany(flag => new[]
            {
                (cleared & flag) != 0 ? Clearing(flag) : MnemonicKind.None,
                (raised & flag) != 0 ? Raising(flag) : MnemonicKind.None,
            }).Where(mnemonic => mnemonic != MnemonicKind.None);
        }
    }

    /// <summary>
    /// Gets the number of bytes the directive emits, which is two for a <c>rep</c> or a <c>sep</c>
    /// and one for each instruction that sets a flag.
    /// </summary>
    public int Length =>
        (Reset != StatusFlags.None ? 2 : 0) + (Set != StatusFlags.None ? 2 : 0) + Instructions.Count();

    /// <summary>
    /// Returns the instructions <paramref name="directive"/> emits when the state reaching it is
    /// <paramref name="before"/>. A width that is not known there is always set, as is every
    /// width of a directive that no state reaches (a null <paramref name="before"/>).
    /// </summary>
    public static Ensured Of(EnsureDirectiveSyntax directive, ProcessorState? before)
    {
        var reset = StatusFlags.None;
        var set = StatusFlags.None;
        foreach (var item in StateItem.Read(directive))
        {
            if (item.Part is not (StatePart.A or StatePart.Index) || item.Width is not (Width.Eight or Width.Sixteen))
                continue;
            var flag = item.Part == StatePart.A ? StatusFlags.M : StatusFlags.X;
            var here = item.Part == StatePart.A ? before?.A : before?.Index;
            if (here == item.Width)
                continue;
            if (item.Width == Width.Sixteen)
                reset |= flag;
            else
                set |= flag;
        }
        return new Ensured(reset, set);
    }

    /// <summary>
    /// Returns these instructions with what sets the flags <paramref name="directive"/> names.
    /// A flag whose value <paramref name="known"/> already gives needs nothing, and every flag
    /// does before the flag analysis has run, when <paramref name="known"/> is null and nothing is
    /// known. A flag that a <c>rep</c> or <c>sep</c> emitted anyway can set is folded into it.
    /// <paramref name="valueOf"/> evaluates an item's value. A flag the directive cannot set, which
    /// the flag analysis reports, emits nothing.
    /// </summary>
    public Ensured WithFlags(EnsureDirectiveSyntax directive, FlagValues? known, Func<ExpressionSyntax, long?> valueOf)
    {
        var ensured = this;
        foreach (var item in StateItem.Read(directive))
        {
            if (item.Part != StatePart.Flag || item.Expression is not { } expression
                || valueOf(expression) is not { } value || value is not (0 or 1))
            {
                continue;
            }
            var one = value == 1;
            foreach (var flag in FlagValues.Named)
            {
                if ((item.Flags & flag) == 0 || known?.ValueOf(flag) == one || Clearing(flag) == MnemonicKind.None
                    || (one && Raising(flag) == MnemonicKind.None))
                {
                    continue;
                }
                ensured = (one, ensured.Set != StatusFlags.None, ensured.Reset != StatusFlags.None) switch
                {
                    (true, true, _) => ensured with { Set = ensured.Set | flag },
                    (true, false, _) => ensured with { Raised = ensured.Raised | flag },
                    (false, _, true) => ensured with { Reset = ensured.Reset | flag },
                    (false, _, false) => ensured with { Cleared = ensured.Cleared | flag },
                };
            }
        }
        return ensured;
    }

    /// <summary>Returns the instruction that clears <paramref name="flag"/>, or <see cref="MnemonicKind.None"/>.</summary>
    private static MnemonicKind Clearing(StatusFlags flag) => flag switch
    {
        StatusFlags.Carry => MnemonicKind.Clc,
        StatusFlags.Decimal => MnemonicKind.Cld,
        StatusFlags.InterruptDisable => MnemonicKind.Cli,
        StatusFlags.Overflow => MnemonicKind.Clv,
        _ => MnemonicKind.None,
    };

    /// <summary>Returns the instruction that sets <paramref name="flag"/>, or <see cref="MnemonicKind.None"/>.</summary>
    private static MnemonicKind Raising(StatusFlags flag) => flag switch
    {
        StatusFlags.Carry => MnemonicKind.Sec,
        StatusFlags.Decimal => MnemonicKind.Sed,
        StatusFlags.InterruptDisable => MnemonicKind.Sei,
        _ => MnemonicKind.None,
    };
}
