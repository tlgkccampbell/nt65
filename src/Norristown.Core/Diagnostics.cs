namespace Norristown;

/// <summary>Diagnostics as a set: how much each matters and how they are ordered.</summary>
public static class Diagnostics
{
    /// <summary>
    /// The same diagnostics, with each severity replaced by the one <paramref name="severities"/>
    /// gives its name: raised to an error, lowered to a warning, or removed when the setting
    /// turns it off.
    /// <para>
    /// A diagnostic already reported as an error is never changed. A project file that asks for
    /// an error to be lowered gets an error of its own there, and this way a construct that is
    /// an error on the 65816 and a warning on other processors cannot be switched off on the
    /// 65816 by a setting written with the other processors in mind.
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
