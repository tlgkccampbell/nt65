namespace Norristown.Flow;

/// <summary>
/// Specifies where a routine runs from, as <see cref="RoutineContexts"/> works it out. A routine
/// may run from both.
/// </summary>
[Flags]
public enum RoutineContext
{
    /// <summary>Neither an interrupt handler nor the rest of the program reaches the routine.</summary>
    None = 0,

    /// <summary>
    /// The rest of the program reaches the routine, from a routine that nothing calls and that is
    /// not an interrupt handler.
    /// </summary>
    Main = 1,

    /// <summary>The routine is an interrupt handler, or a handler reaches it.</summary>
    Interrupt = 2,
}
