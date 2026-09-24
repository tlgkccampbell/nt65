namespace Norristown.Processor;

/// <summary>
/// Represents one build-configuration define, which is a constant visible in every file, as
/// if declared and exported once. Defines are the only symbols an <c>.if</c> condition may
/// test. The output always writes a define as its value rather than by name, so a <c>-D</c>
/// given to ca65 cannot collide with it.
/// </summary>
/// <param name="Name">The name the source uses for the define.</param>
/// <param name="Value">The define's value.</param>
/// <param name="Declaration">Where the define was given: the project file or the command line.</param>
public sealed record Define(string Name, long Value, Span Declaration)
{
    /// <summary>
    /// Gets a value indicating whether this define sets a <c>.config</c> that a module exports,
    /// rather than defining a name of its own. Such a define names the setting with the module's
    /// path, as in <c>hw::SOUND_CHANNELS</c>.
    /// </summary>
    public bool IsSetting => Name.Contains("::", StringComparison.Ordinal);
}
