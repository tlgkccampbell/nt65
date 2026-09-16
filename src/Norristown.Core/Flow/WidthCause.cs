namespace Norristown.Flow;

/// <summary>
/// Why a width is not known, as a message about a width-dependent immediate says it: what made
/// it unknown and what fixes that. A cause the analysis cannot name is none at all, and the
/// message falls back to <c>.state</c>.
/// </summary>
/// <param name="Reason">What made the width unknown, as a clause.</param>
/// <param name="Fix">What to write, as a clause.</param>
public sealed record WidthCause(string Reason, string Fix);
