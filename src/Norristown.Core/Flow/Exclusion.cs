namespace Norristown.Flow;

/// <summary>
/// Something a routine's cost with its calls leaves out, because nt65 cannot count it: a
/// routine with no code in the program, a call or jump through a pointer, a routine that can
/// call itself, or one with no count of its own.
/// </summary>
/// <param name="What">
/// What was left out, as briefly as the editor's lens can name it: the routine's name, the
/// statement nt65 cannot follow, or <c>recursion</c>.
/// </param>
/// <param name="Why">Why nt65 cannot count it, in a phrase, for the hover.</param>
public readonly record struct Exclusion(string What, string Why);
