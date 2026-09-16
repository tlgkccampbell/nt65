namespace Norristown.LanguageServer.Protocol;

/// <summary>What is special about a diagnostic, as LSP numbers it.</summary>
internal enum DiagnosticTag
{
    /// <summary>Code the build does not use, which clients render faded.</summary>
    Unnecessary = 1,

    /// <summary>Code that still works but should not be used, which clients strike through.</summary>
    Deprecated = 2,
}
