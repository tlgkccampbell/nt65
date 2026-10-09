namespace Norristown.Flow;

/// <summary>
/// Specifies what a byte that was already on the stack when a routine was entered is, as its
/// signature declares it.
/// </summary>
public enum EnteredByte
{
    /// <summary>A byte the routine pushed itself, or one whose origin is not known.</summary>
    None,

    /// <summary>A byte the caller pushed before the call, beneath the return address: <c>pushed n</c>.</summary>
    Argument,

    /// <summary>A byte of the return address.</summary>
    ReturnAddress,

    /// <summary>
    /// A byte the routine is entered with above its return address, and pulls before it returns:
    /// <c>pulls n</c>.
    /// </summary>
    Handed,
}
