namespace Norristown.LanguageServer.Protocol;

/// <summary><c>textDocument/inlayHint</c>.</summary>
/// <param name="TextDocument">The document to hint.</param>
/// <param name="Range">The lines the editor is showing; hints are computed only for these.</param>
internal sealed record InlayHintParams(TextDocumentIdentifier TextDocument, Range Range);
