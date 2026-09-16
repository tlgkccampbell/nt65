namespace Norristown.Project;

/// <summary>
/// One build-configuration define (§5.3): a constant visible in every file, as if declared
/// and exported once. Defines are the only symbols an <c>.if</c> condition may test (§10),
/// and the output always writes one as its value rather than by name, so a <c>-D</c> given
/// to ca65 cannot collide with it.
/// </summary>
/// <param name="Name">The name the source writes.</param>
/// <param name="Value">What it is worth.</param>
/// <param name="Declaration">Where it was given: the project file, or the command line.</param>
public sealed record Define(string Name, long Value, Span Declaration);
