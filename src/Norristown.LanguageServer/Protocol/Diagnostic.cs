namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents a problem as the client shows it.</summary>
/// <param name="Range">The range the problem is reported at.</param>
/// <param name="Severity">How serious the problem is.</param>
/// <param name="Code">The catalogue name the diagnostic is reported under.</param>
/// <param name="Source">The tool that reported the problem, which is always <c>nt65</c>.</param>
/// <param name="Message">The message shown to the programmer.</param>
/// <param name="RelatedInformation">Other locations that explain the problem, or null.</param>
/// <param name="Tags">Tags that change how the client renders the problem, or null.</param>
internal sealed record Diagnostic(
    Range Range,
    DiagnosticSeverity Severity,
    string Code,
    string Source,
    string Message,
    IReadOnlyList<DiagnosticRelatedInformation>? RelatedInformation,
    IReadOnlyList<DiagnosticTag>? Tags = null);
