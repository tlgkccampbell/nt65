namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Parameters of the <c>nt65/output</c> request, which asks for the output of one source file as
/// the editor currently holds it.
/// </summary>
/// <param name="TextDocument">The source file.</param>
internal sealed record OutputParams(TextDocumentIdentifier TextDocument);
