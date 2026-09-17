namespace Norristown.LanguageServer.Protocol;

/// <summary>The part of a document the client shows, which is what it wants hints for.</summary>
internal sealed record InlayHintParams(TextDocumentIdentifier TextDocument, Range Range);
