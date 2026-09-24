namespace Norristown;

/// <summary>
/// Represents one diagnostic as plain data, made up of a span, a catalogue name, a severity, a
/// message and optional related spans.
/// </summary>
/// <param name="Span">The span where the diagnostic is reported.</param>
/// <param name="Id">The catalogue name it is reported under.</param>
/// <param name="Severity">How much it matters.</param>
/// <param name="Message">The message shown to the programmer.</param>
/// <param name="Related">Other spans that help explain it.</param>
public sealed record Diagnostic(
    Span Span, string Id, Severity Severity, string Message, IReadOnlyList<RelatedSpan> Related)
{
    /// <summary>
    /// Initializes a diagnostic that reports <paramref name="message"/> at the severity the
    /// catalogue gives it.
    /// </summary>
    public Diagnostic(Span span, DiagnosticMessage message)
        : this(span, message.Descriptor.Id, message.Descriptor.Severity, message.Text, []) { }

    /// <summary>
    /// Initializes a diagnostic that reports <paramref name="message"/> at the severity the
    /// catalogue gives it, with <paramref name="related"/> spans that explain it.
    /// </summary>
    public Diagnostic(Span span, DiagnosticMessage message, IReadOnlyList<RelatedSpan> related)
        : this(span, message.Descriptor.Id, message.Descriptor.Severity, message.Text, related) { }

    /// <summary>
    /// Initializes a diagnostic that reports <paramref name="message"/> at a
    /// <paramref name="severity"/> chosen by the reporting site rather than the catalogue. A
    /// construct that is an error on the 65816 and only a warning on earlier processors is one
    /// diagnostic, reported at a different severity on each.
    /// </summary>
    public Diagnostic(Span span, Severity severity, DiagnosticMessage message)
        : this(span, message.Descriptor.Id, severity, message.Text, []) { }

    /// <summary>
    /// Initializes a diagnostic that reports <paramref name="message"/> at a
    /// <paramref name="severity"/> chosen by the reporting site, with <paramref name="related"/>
    /// spans that explain it.
    /// </summary>
    public Diagnostic(Span span, Severity severity, DiagnosticMessage message, IReadOnlyList<RelatedSpan> related)
        : this(span, message.Descriptor.Id, severity, message.Text, related) { }

    /// <summary>
    /// Gets the change that the message names as the fix, for an editor to offer, or null.
    /// </summary>
    public DiagnosticFix? Fix { get; init; }

    /// <summary>
    /// Gets a value indicating whether the code the diagnostic is about is not needed, such as a
    /// declaration nothing names or a name imported and never used. An editor shows such code
    /// faded, the usual look for unnecessary code, rather than only listing it.
    /// </summary>
    public bool IsUnnecessary { get; init; }
}
