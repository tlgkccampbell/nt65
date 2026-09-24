namespace Norristown.LanguageServer.Protocol;

/// <summary>Specifies how the client sends document changes, as LSP numbers it.</summary>
internal enum TextDocumentSyncKind
{
    /// <summary>The client sends no changes.</summary>
    None = 0,

    /// <summary>The client sends the whole document on every change.</summary>
    Full = 1,

    /// <summary>The client sends only the ranges that changed, which is what nt65 asks for.</summary>
    Incremental = 2,
}
