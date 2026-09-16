namespace Norristown.LanguageServer.Protocol;

/// <summary>How the client sends changes, as LSP numbers it.</summary>
internal enum TextDocumentSyncKind
{
    /// <summary>Not at all.</summary>
    None = 0,

    /// <summary>The whole document, every time.</summary>
    Full = 1,

    /// <summary>Only the ranges that changed, which is what nt65 asks for.</summary>
    Incremental = 2,
}
