namespace Norristown;

/// <summary>A range on one line. Lines and columns are 1-based; <see cref="EndColumn"/> is exclusive.</summary>
public readonly record struct Span(string File, int Line, int StartColumn, int EndColumn);

public enum Severity { Error, Warning, Info }

public sealed record RelatedSpan(Span Span, string Message);

/// <summary>Diagnostics are data: a span, a severity, a message and optional related spans.</summary>
public sealed record Diagnostic(Span Span, Severity Severity, string Message, IReadOnlyList<RelatedSpan> Related)
{
    public Diagnostic(Span span, Severity severity, string message)
        : this(span, severity, message, []) { }
}
