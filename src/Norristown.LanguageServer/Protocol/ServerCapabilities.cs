namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Describes what the server can do, as announced in its answer to <c>initialize</c>.
/// </summary>
/// <param name="TextDocumentSync">How open documents are kept in sync.</param>
/// <param name="DocumentSymbolProvider">Whether the server answers <c>textDocument/documentSymbol</c>.</param>
/// <param name="FoldingRangeProvider">Whether the server answers <c>textDocument/foldingRange</c>.</param>
/// <param name="HoverProvider">Whether the server answers <c>textDocument/hover</c>.</param>
/// <param name="DefinitionProvider">Whether the server answers <c>textDocument/definition</c>.</param>
/// <param name="ReferencesProvider">Whether the server answers <c>textDocument/references</c>.</param>
/// <param name="DocumentHighlightProvider">
/// Whether the server answers <c>textDocument/documentHighlight</c>.
/// </param>
/// <param name="RenameProvider">How the server provides renames, or null when it does not.</param>
/// <param name="CompletionProvider">How the server provides completion, or null when it does not.</param>
/// <param name="SignatureHelpProvider">
/// How the server provides signature help, or null when it does not.
/// </param>
/// <param name="CodeLensProvider">
/// How the server answers <c>textDocument/codeLens</c>, or null when it does not.
/// </param>
/// <param name="WorkspaceSymbolProvider">Whether the server answers <c>workspace/symbol</c>.</param>
/// <param name="CodeActionProvider">
/// The kinds of code action the server offers, or null when it offers none.
/// </param>
/// <param name="SemanticTokensProvider">
/// How the server classifies names, or null when it does not.
/// </param>
/// <param name="InlayHintProvider">How the server provides inlay hints, or null when it does not.</param>
/// <param name="SelectionRangeProvider">
/// Whether the server answers <c>textDocument/selectionRange</c> (expand selection).
/// </param>
/// <param name="CallHierarchyProvider">Whether the server answers the three call-hierarchy requests.</param>
/// <param name="DocumentLinkProvider">
/// How the server answers <c>textDocument/documentLink</c>, or null when it does not.
/// </param>
/// <param name="DocumentFormattingProvider">Whether the server formats a whole file.</param>
/// <param name="DocumentRangeFormattingProvider">Whether the server formats part of a file.</param>
/// <param name="PositionEncoding">
/// How a character offset within a line is counted, which is in UTF-16 code units.
/// </param>
/// <param name="Workspace">
/// What the server does with the workspace itself, or null when it does nothing.
/// </param>
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
    bool SelectionRangeProvider = false,
    bool CallHierarchyProvider = false,
    DocumentLinkOptions? DocumentLinkProvider = null,
    bool DocumentFormattingProvider = false,
    bool DocumentRangeFormattingProvider = false,
    string? PositionEncoding = null,
    WorkspaceServerCapabilities? Workspace = null);
