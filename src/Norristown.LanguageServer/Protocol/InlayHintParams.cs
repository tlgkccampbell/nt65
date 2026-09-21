namespace Norristown.LanguageServer.Protocol;

/// <summary><c>textDocument/inlayHint</c>.</summary>
/// <param name="TextDocument">The document to hint.</param>
/// <param name="Range">The lines the editor is showing, which are the only ones worked out.</param>
internal sealed record InlayHintParams(TextDocumentIdentifier TextDocument, Range Range);
