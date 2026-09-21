namespace Norristown.LanguageServer.Protocol;

/// <summary>One problem, as the client shows it.</summary>
/// <param name="Range">Where it is reported.</param>
/// <param name="Severity">How much it matters.</param>
/// <param name="Code">The catalogue name it is reported under.</param>
/// <param name="Source">Who reported it; always <c>nt65</c>.</param>
/// <param name="Message">What to tell the programmer.</param>
/// <param name="RelatedInformation">Other places that explain it, or null.</param>
/// <param name="Tags">What is special about it, or null.</param>
internal sealed record Diagnostic(
    Range Range,
    DiagnosticSeverity Severity,
    string Code,
    string Source,
    string Message,
    IReadOnlyList<DiagnosticRelatedInformation>? RelatedInformation,
    IReadOnlyList<DiagnosticTag>? Tags = null);
