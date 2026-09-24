namespace Norristown.Processor;

/// <summary>
/// Represents one value the build gives a setting, from the project file or the command line. A
/// setting is a constant a module declares with <c>?=</c>, and the build names it by its path,
/// as in <c>hw::SOUND_CHANNELS</c>, or by its name alone where only one module declares a setting
/// of that name. The output always writes a setting as its value rather than by name, so a
/// <c>-D</c> given to ca65 cannot collide with it.
/// </summary>
/// <param name="Name">The setting's path or name, as the build gives it.</param>
/// <param name="Value">The setting's value.</param>
/// <param name="Declaration">Where the value was given: the project file or the command line.</param>
public sealed record SettingValue(string Name, long Value, Span Declaration)
{
    /// <summary>
    /// Gets a value indicating whether the name has a module's path, as in <c>hw::PAL</c>, rather
    /// than being a setting's name alone.
    /// </summary>
    public bool HasPath => Name.Contains("::", StringComparison.Ordinal);
}
