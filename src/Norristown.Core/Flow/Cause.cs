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
    /// Returns the cause for a stack that a call leaves unknown, because nt65 cannot work out what
    /// the routine <paramref name="call"/> reaches leaves on its caller's stack.
    /// </summary>
    public static Cause CallLeavesUnknown(string call) => new(
        $"what {call} leaves on the stack is not known",
        "keep what is pushed before it off the stack across it, or make every way that routine returns "
            + "leave the same number of bytes");

    /// <summary>
    /// Returns the cause for a stack whose saved bytes the store <paramref name="store"/> may have
    /// changed. nt65 does not follow which byte a store into the stack changes, so what a pull or
    /// an <c>rti</c> restores after it is not known.
    /// </summary>
    public static Cause StackWritten(string store) => new(
        $"{store} writes into the bytes on the stack",
        "nt65 does not follow which byte it changes, so what a pull or an `rti` restores after it is not known");

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
