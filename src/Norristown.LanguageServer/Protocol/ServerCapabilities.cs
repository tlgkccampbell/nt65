namespace Norristown.LanguageServer.Protocol;

/// <summary>What the server can do. Stages add to this as the layers come online.</summary>
/// <param name="TextDocumentSync">How open documents are kept in step.</param>
/// <param name="DocumentSymbolProvider">Whether the server answers <c>textDocument/documentSymbol</c>.</param>
/// <param name="FoldingRangeProvider">Whether the server answers <c>textDocument/foldingRange</c>.</param>
/// <param name="HoverProvider">Whether the server answers <c>textDocument/hover</c>.</param>
/// <param name="DefinitionProvider">Whether the server answers <c>textDocument/definition</c>.</param>
/// <param name="ReferencesProvider">Whether the server answers <c>textDocument/references</c>.</param>
/// <param name="DocumentHighlightProvider">Whether the server answers <c>textDocument/documentHighlight</c>.</param>
/// <param name="RenameProvider">How the server renames, or null when it does not.</param>
/// <param name="CompletionProvider">How the server completes, or null when it does not.</param>
/// <param name="SignatureHelpProvider">How the server helps with calls, or null when it does not.</param>
/// <param name="CodeLensProvider">How the server answers <c>textDocument/codeLens</c>, or null when it does not.</param>
/// <param name="WorkspaceSymbolProvider">Whether the server answers <c>workspace/symbol</c>.</param>
/// <param name="CodeActionProvider">Which kinds of change the server offers, or null when it offers none.</param>
/// <param name="SemanticTokensProvider">How the server classifies names, or null when it does not.</param>
/// <param name="InlayHintProvider">How the server hints in a line, or null when it does not.</param>
/// <param name="CallHierarchyProvider">Whether the server answers the three call-hierarchy requests.</param>
/// <param name="DocumentLinkProvider">How the server answers <c>textDocument/documentLink</c>, or null when it does not.</param>
/// <param name="DocumentFormattingProvider">Whether the server lays a whole file out.</param>
/// <param name="DocumentRangeFormattingProvider">Whether the server lays part of a file out.</param>
/// <param name="PositionEncoding">How a character offset in a line is counted, which is UTF-16 code units.</param>
/// <param name="Workspace">What the server does about the workspace itself, or null where it does nothing.</param>
internal sealed record ServerCapabilities(
    TextDocumentSyncOptions TextDocumentSync,
    bool DocumentSymbolProvider,
    bool FoldingRangeProvider,
    bool HoverProvider,
    bool DefinitionProvider,
    bool ReferencesProvider,
    bool DocumentHighlightProvider,
    RenameOptions? RenameProvider,
    CompletionOptions? CompletionProvider = null,
    SignatureHelpOptions? SignatureHelpProvider = null,
    CodeLensOptions? CodeLensProvider = null,
    bool WorkspaceSymbolProvider = false,
    CodeActionOptions? CodeActionProvider = null,
    SemanticTokensOptions? SemanticTokensProvider = null,
    InlayHintOptions? InlayHintProvider = null,
    bool CallHierarchyProvider = false,
    DocumentLinkOptions? DocumentLinkProvider = null,
    bool DocumentFormattingProvider = false,
    bool DocumentRangeFormattingProvider = false,
    string? PositionEncoding = null,
    WorkspaceServerCapabilities? Workspace = null);
