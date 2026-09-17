namespace Norristown.LanguageServer.Protocol;

/// <summary>Where the client asks what can be done, and the diagnostics it shows there.</summary>
internal sealed record CodeActionParams(TextDocumentIdentifier TextDocument, Range Range, CodeActionContext Context);
