namespace Norristown.Flow;

/// <summary>
/// Represents why something the analysis tracks is not known, worded for the diagnostic on the
/// line that needs it. It names what made the value unknown and what fixes that. Where the
/// analysis cannot name a cause there is no <see cref="Cause"/> at all, and the diagnostic is
/// reported without a reason.
/// </summary>
/// <param name="Reason">What made it unknown, as a clause.</param>
/// <param name="Fix">What to put in the source to fix it, as a clause.</param>
public sealed record Cause(string Reason, string Fix)
{
    /// <summary>
    /// Returns the clause a message ends with to say why and what fixes it, or an empty string
    /// when there is no cause to name.
    /// </summary>
    public static string Because(Cause? cause) =>
        cause is null ? "" : $", because {cause.Reason}: {cause.Fix}";

    /// <summary>
    /// Returns the cause for a stack that two paths leave unknown where they meet, because they
    /// pushed different things. <paramref name="depths"/> says whether they pushed different
    /// amounts, which is the usual reason.
    /// </summary>
    public static Cause StacksDiffer(bool depths) => depths
        ? new("two paths meet above it having pushed different amounts",
            "pulling on each path what it pushed before they meet keeps the stack known")
        : new("two paths meet above it having pushed different things",
            "pushing the same things on each path before they meet keeps the stack known");
}
