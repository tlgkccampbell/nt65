namespace Norristown.LanguageServer.Protocol;

/// <summary>Specifies how serious a diagnostic is, as LSP numbers it.</summary>
internal enum DiagnosticSeverity
{
    /// <summary>An error.</summary>
    Error = 1,

    /// <summary>A warning.</summary>
    Warning = 2,

    /// <summary>Information worth showing that is neither an error nor a warning.</summary>
    Information = 3,

    /// <summary>A hint, which editors render faintly or not at all.</summary>
    Hint = 4,
}
