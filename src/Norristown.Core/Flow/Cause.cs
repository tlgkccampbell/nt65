namespace Norristown.Flow;

/// <summary>
/// Why something the analysis tracks is not known, worded for the diagnostic on the line that
/// needs it: what made it unknown, and what fixes that. Where the analysis cannot name a cause
/// there is no <see cref="Cause"/> at all, and the diagnostic is given without a reason.
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
