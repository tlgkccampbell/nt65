namespace Norristown.Flow;

/// <summary>
/// Why something the analysis follows is not known, as a message about the line that needs it
/// says it: what made it unknown and what fixes that. A cause the analysis cannot name is none
/// at all, and the message falls back to what it says with no reason to give.
/// </summary>
/// <param name="Reason">What made it unknown, as a clause.</param>
/// <param name="Fix">What to write, as a clause.</param>
public sealed record Cause(string Reason, string Fix)
{
    /// <summary>
    /// The clause a message ends with to say why and what to write, or nothing where there is
    /// no cause to name.
    /// </summary>
    public static string Because(Cause? cause) =>
        cause is null ? "" : $", because {cause.Reason}: {cause.Fix}";
}
