namespace Norristown.Flow;

/// <summary>Specifies what is known about a <see cref="StackEffect"/>.</summary>
public enum StackEffectKind
{
    /// <summary>
    /// No path is known to return to the caller, so nothing the caller does after the call is
    /// reached. A routine starts here until a path that returns is found.
    /// </summary>
    NeverReturns,

    /// <summary>Every path that returns leaves the same number of bytes on the caller's stack.</summary>
    Leaves,

    /// <summary>
    /// What a return leaves on the caller's stack is not known, or differs between the paths that
    /// return.
    /// </summary>
    Unknown,
}
