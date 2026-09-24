using System.Collections.Immutable;

namespace Norristown.Semantics;

/// <summary>
/// Explains why the configuration alone does not decide a value that a condition, or a
/// setting's default, uses. The notes run from the value that was used to the cause, one
/// declaration at a time, such as "`VOICES` uses `per_voice`" followed by "`per_voice`
/// measures `Voice` with `.sizeof`".
/// </summary>
/// <param name="Cause">What, at the end of the chain, the configuration does not decide.</param>
/// <param name="Notes">The steps from the value that was used to the cause, each at the declaration it is about.</param>
/// <param name="Root">The name of the declaration the cause is about, which a fix names, or null.</param>
internal sealed record Undecided(UndecidedCause Cause, ImmutableArray<RelatedSpan> Notes, string? Root = null)
{
    /// <summary>Gets the reason given for a measurement written directly where it is used.</summary>
    public static Undecided Measured { get; } = new(UndecidedCause.Measurement, []);

    /// <summary>
    /// Returns this reason as the reason for <paramref name="user"/>, declared at
    /// <paramref name="at"/>, which uses <paramref name="used"/>.
    /// </summary>
    public Undecided Through(Span at, string user, string used) =>
        this with { Notes = Notes.Insert(0, new RelatedSpan(at, $"`{user}` uses `{used}`")) };
}
