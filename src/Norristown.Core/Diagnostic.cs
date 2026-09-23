namespace Norristown;

/// <summary>Diagnostics are data: a span, a name, a severity, a message and optional related spans.</summary>
/// <param name="Span">Where it is reported.</param>
/// <param name="Id">The catalogue name it is reported under.</param>
/// <param name="Severity">How much it matters.</param>
/// <param name="Message">What to tell the programmer.</param>
/// <param name="Related">Other places that explain it.</param>
public sealed record Diagnostic(
    Span Span, string Id, Severity Severity, string Message, IReadOnlyList<RelatedSpan> Related)
{
    /// <summary>A diagnostic that reports <paramref name="message"/> at the severity the catalogue gives it.</summary>
    public Diagnostic(Span span, DiagnosticMessage message)
        : this(span, message.Descriptor.Id, message.Descriptor.Severity, message.Text, []) { }

    /// <summary>The same, with the places that explain it.</summary>
    public Diagnostic(Span span, DiagnosticMessage message, IReadOnlyList<RelatedSpan> related)
        : this(span, message.Descriptor.Id, message.Descriptor.Severity, message.Text, related) { }

    /// <summary>
    /// The same, with a severity chosen by the reporting site rather than the catalogue: a
    /// construct that is an error on the 65816 and only a warning on earlier processors is one
    /// diagnostic, reported at a different severity on each.
    /// </summary>
    public Diagnostic(Span span, Severity severity, DiagnosticMessage message)
        : this(span, message.Descriptor.Id, severity, message.Text, []) { }

    /// <summary>The same, with the places that explain it.</summary>
    public Diagnostic(Span span, Severity severity, DiagnosticMessage message, IReadOnlyList<RelatedSpan> related)
        : this(span, message.Descriptor.Id, severity, message.Text, related) { }

    /// <summary>The change its message names as the fix, for an editor to offer, or null.</summary>
    public DiagnosticFix? Fix { get; init; }

    /// <summary>
    /// Whether what it is about is not needed: a declaration nothing names, or a name brought
    /// in and never written. An editor fades those rather than only listing them, which is how
    /// something unnecessary is meant to read.
    /// </summary>
    public bool IsUnnecessary { get; init; }
}
