namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents an <c>nt65/widths</c> request, which asks for the register widths on each line of a document.</summary>
/// <param name="TextDocument">The document.</param>
internal sealed record WidthsParams(TextDocumentIdentifier TextDocument);
