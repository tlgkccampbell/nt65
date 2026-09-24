namespace Norristown.LanguageServer.Protocol;

/// <summary>Parameters of the <c>textDocument/inlayHint</c> request.</summary>
/// <param name="TextDocument">The document to provide hints for.</param>
/// <param name="Range">
/// The lines the editor is showing. Hints are computed only for these lines.
/// </param>
internal sealed record InlayHintParams(TextDocumentIdentifier TextDocument, Range Range);
