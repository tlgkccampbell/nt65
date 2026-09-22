namespace Norristown.Processor;

/// <summary>
/// One build-configuration define: a constant visible in every file, as if declared
/// and exported once. Defines are the only symbols an <c>.if</c> condition may test,
/// and the output always writes one as its value rather than by name, so a <c>-D</c> given
/// to ca65 cannot collide with it.
/// </summary>
/// <param name="Name">The name the source writes.</param>
/// <param name="Value">What it is worth.</param>
/// <param name="Declaration">Where it was given: the project file, or the command line.</param>
public sealed record Define(string Name, long Value, Span Declaration)
{
    /// <summary>
    /// Whether this sets a <c>.config</c> a module exports, written with the module's path as in
    /// <c>hw::SOUND_CHANNELS</c>, rather than defining a name of its own.
    /// </summary>
    public bool IsSetting => Name.Contains("::", StringComparison.Ordinal);
}
