namespace Norristown;

/// <summary>Diagnostics as a set: how they are ordered, wherever they come from.</summary>
public static class Diagnostics
{
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
    public static IReadOnlyList<Diagnostic> Ordered(IEnumerable<Diagnostic> diagnostics) =>
        [.. diagnostics
            .DistinctBy(d => (d.Span, d.Severity, d.Id, d.Message))
            .OrderBy(d => d.Span.File, StringComparer.Ordinal)
            .ThenBy(d => d.Span.Line)
            .ThenBy(d => d.Span.StartColumn)
            .ThenBy(d => d.Message, StringComparer.Ordinal)
            .ThenBy(d => d.Id, StringComparer.Ordinal)];
}
