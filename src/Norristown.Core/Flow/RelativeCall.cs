using Norristown.Semantics;

namespace Norristown.Flow;

/// <summary>
/// Represents a call made with a branch, which is how position-independent code calls. The
/// sequence is <c>per L-1</c> then <c>brl f</c> or <c>bra f</c>, where <c>L</c> labels the
/// statement after the branch. It pushes the address a return comes back to, as <c>jsr</c>
/// does. With <c>phk</c> before the <c>per</c> it pushes the bank too, as <c>jsl</c> does.
/// </summary>
/// <param name="Routine">The routine the branch goes to.</param>
/// <param name="IsFar">Whether a <c>phk</c> pushed the bank as well, so it returns with <c>rtl</c>.</param>
public readonly record struct RelativeCall(Symbol Routine, bool IsFar)
{
    /// <summary>Gets how many bytes the call pushed, which the routine's return pulls.</summary>
    public int Pushed => IsFar ? 3 : 2;
}
