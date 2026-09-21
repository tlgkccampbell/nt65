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
    /// <summary>What <paramref name="message"/> says, where the catalogue's severity is right.</summary>
    public Diagnostic(Span span, DiagnosticMessage message)
        : this(span, message.Descriptor.Id, message.Descriptor.Severity, message.Text, []) { }

    /// <summary>The same, with the places that explain it.</summary>
    public Diagnostic(Span span, DiagnosticMessage message, IReadOnlyList<RelatedSpan> related)
        : this(span, message.Descriptor.Id, message.Descriptor.Severity, message.Text, related) { }

    /// <summary>
    /// The same, where how much it matters is the site's to say: a construct the 65816 refuses
    /// and an earlier processor only wonders about is one diagnostic, said twice as loudly.
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
