namespace Norristown;

/// <summary>Diagnostics are data: a span, a severity, a message and optional related spans.</summary>
/// <param name="Span">Where it is reported.</param>
/// <param name="Severity">How much it matters.</param>
/// <param name="Message">What to tell the programmer.</param>
/// <param name="Related">Other places that explain it.</param>
public sealed record Diagnostic(Span Span, Severity Severity, string Message, IReadOnlyList<RelatedSpan> Related)
{
    /// <summary>A diagnostic with no related spans.</summary>
    public Diagnostic(Span span, Severity severity, string message)
        : this(span, severity, message, []) { }

    /// <summary>The change its message names as the fix, for an editor to offer, or null.</summary>
    public DiagnosticFix? Fix { get; init; }

    /// <summary>
    /// Whether what it is about is not needed: a declaration nothing names, or a name brought
    /// in and never written. An editor fades those rather than only listing them, which is how
    /// something unnecessary is meant to read.
    /// </summary>
    public bool IsUnnecessary { get; init; }
}
