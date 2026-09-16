namespace Norristown.LanguageServer.Protocol;

/// <summary>How much a diagnostic matters, as LSP numbers it.</summary>
internal enum DiagnosticSeverity
{
    /// <summary>An error.</summary>
    Error = 1,

    /// <summary>A warning.</summary>
    Warning = 2,

    /// <summary>Something worth showing that is neither.</summary>
    Information = 3,

    /// <summary>A hint, which editors render faintly or not at all.</summary>
    Hint = 4,
}
