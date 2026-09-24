namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Parameters of the <c>textDocument/codeAction</c> request, which gives the range where the client
/// asks what can be done and the diagnostics it shows there.
/// </summary>
/// <param name="TextDocument">The document.</param>
/// <param name="Range">The range the client asks about.</param>
/// <param name="Context">The diagnostics shown at the range and the kinds of action wanted.</param>
internal sealed record CodeActionParams(TextDocumentIdentifier TextDocument, Range Range, CodeActionContext Context);
