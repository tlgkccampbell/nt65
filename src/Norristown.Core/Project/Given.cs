namespace Norristown.Project;

/// <summary>
/// Represents a number that a project file or a linker configuration gives. The number itself is
/// null when nt65 cannot work it out, which is different from not being given at all.
/// </summary>
/// <param name="Value">The number, or null when it is unknown.</param>
public readonly record struct Given(long? Value);
