namespace Norristown.Flow;

/// <summary>
/// Represents something that a routine's cost with its calls leaves out because nt65 cannot
/// count it. It may be a routine with no code in the program, a call or jump through a pointer,
/// a routine that can call itself, or a routine with no count of its own.
/// </summary>
/// <param name="What">
/// What was left out, named as briefly as the editor's lens allows. This is the routine's name,
/// the statement nt65 cannot follow, or <c>recursion</c>.
/// </param>
/// <param name="Why">A phrase for the hover saying why nt65 cannot count it.</param>
public readonly record struct Exclusion(string What, string Why);
