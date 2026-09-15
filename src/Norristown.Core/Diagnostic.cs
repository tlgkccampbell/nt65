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
}
