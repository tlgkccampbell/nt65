using Norristown.Semantics;

namespace Norristown.Flow;

/// <summary>
/// A call written as a branch: <c>per L-1</c> then <c>brl f</c> or <c>bra f</c>, where
/// <c>L</c> labels the statement after the branch. It pushes the address a return comes back
/// to, as <c>jsr</c> does, and with <c>phk</c> before the <c>per</c> the bank too, as
/// <c>jsl</c> does, which is how position-independent code calls.
/// </summary>
/// <param name="Routine">The routine the branch goes to.</param>
/// <param name="IsFar">Whether a <c>phk</c> pushed the bank as well, so it returns with <c>rtl</c>.</param>
public readonly record struct RelativeCall(Symbol Routine, bool IsFar)
{
    /// <summary>How many bytes the call pushed, which the routine's return pulls.</summary>
    public int Pushed => IsFar ? 3 : 2;
}
