namespace Norristown.LanguageServer.Protocol;

/// <summary><c>nt65/output</c>: what one source file became, as the editor holds it now.</summary>
/// <param name="TextDocument">The source file.</param>
internal sealed record OutputParams(TextDocumentIdentifier TextDocument);
