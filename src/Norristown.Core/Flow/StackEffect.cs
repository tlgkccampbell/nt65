namespace Norristown.Flow;

/// <summary>
/// Represents what a call to a routine does to its caller's stack once the routine returns: how
/// many bytes the caller's stack holds then beyond what it held before the call. Most routines
/// leave nothing. One that pulls its own return address and returns through it may leave bytes
/// it pushed, or take bytes its caller pushed, which is a negative count.
/// <para>
/// <see cref="StackEffects"/> works the effect out from each routine's body, over the whole
/// program. A routine starts at <see cref="NeverReturns"/>, and each path found to return is
/// joined in, so the effect only moves towards <see cref="Unknown"/>.
/// </para>
/// </summary>
/// <param name="Kind">What is known.</param>
/// <param name="Bytes">
/// How many bytes a return leaves on the caller's stack, where <paramref name="Kind"/> is
/// <see cref="StackEffectKind.Leaves"/>, and 0 otherwise.
/// </param>
public readonly record struct StackEffect(StackEffectKind Kind, int Bytes)
{
    /// <summary>Gets the effect of a routine no path is known to return from.</summary>
    public static StackEffect NeverReturns => new(StackEffectKind.NeverReturns, 0);

    /// <summary>Gets the effect of a routine that leaves its caller's stack as it found it.</summary>
    public static StackEffect Balanced => new(StackEffectKind.Leaves, 0);

    /// <summary>Gets the effect of a routine whose returns leave something not known.</summary>
    public static StackEffect Unknown => new(StackEffectKind.Unknown, 0);

    /// <summary>
    /// Gets a value indicating whether a call leaves the caller's stack as it was before the call,
    /// which is so where the routine is balanced and where it never returns.
    /// </summary>
    public bool KeepsTheStack => Kind == StackEffectKind.NeverReturns || this == Balanced;

    /// <summary>Returns the effect of a routine whose returns leave <paramref name="bytes"/> bytes.</summary>
    public static StackEffect Leaving(int bytes) => new(StackEffectKind.Leaves, bytes);

    /// <summary>
    /// Returns the effect of a routine that returns along the paths of both <paramref name="a"/>
    /// and <paramref name="b"/>. Paths that leave different counts give an unknown effect, because
    /// a caller cannot tell which path ran.
    /// </summary>
    public static StackEffect Join(StackEffect a, StackEffect b) =>
        a.Kind == StackEffectKind.NeverReturns ? b
            : b.Kind == StackEffectKind.NeverReturns ? a
            : a == b ? a
            : Unknown;

    /// <inheritdoc/>
    public override string ToString() => Kind switch
    {
        StackEffectKind.NeverReturns => "never returns",
        StackEffectKind.Leaves => Bytes == 0 ? "balanced" : $"leaves {Bytes}",
        _ => "unknown",
    };
}
