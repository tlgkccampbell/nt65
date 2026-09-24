namespace Norristown;

/// <summary>
/// Provides operations on a set of diagnostics, which decide how much each one matters and the
/// order they are reported in.
/// </summary>
public static class Diagnostics
{
    /// <summary>
    /// Returns the diagnostics with each severity replaced by the one that
    /// <paramref name="severities"/> gives its name. A setting can raise a diagnostic to an error
    /// or lower it to a warning, and a diagnostic is removed when its setting turns it off.
    /// <para>
    /// A diagnostic already reported as an error is never changed. A project file that asks for
    /// an error to be lowered gets an error of its own there. As a result, a setting written with
    /// other processors in mind cannot switch off, on the 65816, a construct that is an error on
    /// the 65816 and a warning on other processors.
    /// </para>
    /// </summary>
    /// <param name="diagnostics">The diagnostics the analysis found.</param>
    /// <param name="severities">
    /// The severity the project file sets for each diagnostic name, or null for a name it turns off.
    /// </param>
    public static IEnumerable<Diagnostic> WithSeverities(
        IEnumerable<Diagnostic> diagnostics, IReadOnlyDictionary<string, Severity?> severities)
    {
        if (severities.Count == 0)
            return diagnostics;
        return diagnostics
            .Where(d => d.Severity == Severity.Error || !severities.TryGetValue(d.Id, out var off) || off is not null)
            .Select(d => d.Severity != Severity.Error && severities.TryGetValue(d.Id, out var said) && said is { } level
                ? d with { Severity = level }
                : d);
    }

    /// <summary>
    /// Returns the diagnostics in the order they are reported: by file, line and column, and then
    /// by message and name. A program therefore reports the same list regardless of the order its
    /// files arrived in or its layers ran in.
    /// <para>
    /// A diagnostic reported twice at the same span, with the same severity, name and message, is
    /// reported once. A macro body is laid out and emitted at every call that expands it, so a
    /// mistake in it that does not depend on the arguments is found once per call and is still
    /// one mistake.
    /// </para>
    /// </summary>
    /// <param name="diagnostics">The diagnostics the analysis found.</param>
    public static IReadOnlyList<Diagnostic> Ordered(IEnumerable<Diagnostic> diagnostics) =>
        [.. diagnostics
            .DistinctBy(d => (d.Span, d.Severity, d.Id, d.Message))
            .OrderBy(d => d.Span.File, StringComparer.Ordinal)
            .ThenBy(d => d.Span.Line)
            .ThenBy(d => d.Span.StartColumn)
            .ThenBy(d => d.Message, StringComparer.Ordinal)
            .ThenBy(d => d.Id, StringComparer.Ordinal)];
}
