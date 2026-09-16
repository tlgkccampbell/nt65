namespace Norristown;

/// <summary>Diagnostics as a set: how they are ordered, wherever they come from.</summary>
public static class Diagnostics
{
    /// <summary>
    /// The order diagnostics are reported in: by file, line and column, and then by message,
    /// so that a program reports the same list whatever order its files arrived in or its
    /// layers ran in.
    /// </summary>
    public static IReadOnlyList<Diagnostic> Ordered(IEnumerable<Diagnostic> diagnostics) =>
        [.. diagnostics
            .OrderBy(d => d.Span.File, StringComparer.Ordinal)
            .ThenBy(d => d.Span.Line)
            .ThenBy(d => d.Span.StartColumn)
            .ThenBy(d => d.Message, StringComparer.Ordinal)];
}
