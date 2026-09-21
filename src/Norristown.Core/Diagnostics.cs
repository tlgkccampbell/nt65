namespace Norristown;

/// <summary>Diagnostics as a set: how much each matters and how they are ordered.</summary>
public static class Diagnostics
{
    /// <summary>
    /// The same diagnostics, reported as <paramref name="severities"/> says by name: one turned
    /// up to an error, one turned down to a warning, and one turned off gone.
    /// <para>
    /// An error is left alone. A project is told where it asks for one to be turned down, and
    /// a construct that is an error on the 65816 and a warning elsewhere is not switched off
    /// where it matters by a line that meant the other processor.
    /// </para>
    /// </summary>
    /// <param name="diagnostics">What the analysis found.</param>
    /// <param name="severities">What the project file says about each name.</param>
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
    /// The order diagnostics are reported in: by file, line and column, and then by message and
    /// name, so that a program reports the same list whatever order its files arrived in or its
    /// layers ran in.
    /// <para>
    /// The same thing said twice about the same place is said once. A macro body is laid out
    /// and written at every call that expands it, so a mistake in one that does not depend on
    /// the arguments is found once per call and is still one mistake.
    /// </para>
    /// </summary>
    /// <param name="diagnostics">What the analysis found.</param>
    public static IReadOnlyList<Diagnostic> Ordered(IEnumerable<Diagnostic> diagnostics) =>
        [.. diagnostics
            .DistinctBy(d => (d.Span, d.Severity, d.Id, d.Message))
            .OrderBy(d => d.Span.File, StringComparer.Ordinal)
            .ThenBy(d => d.Span.Line)
            .ThenBy(d => d.Span.StartColumn)
            .ThenBy(d => d.Message, StringComparer.Ordinal)
            .ThenBy(d => d.Id, StringComparer.Ordinal)];
}
