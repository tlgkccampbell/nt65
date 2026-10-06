namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Parameters of the <c>nt65/directPages</c> request, which asks for the direct page map of the
/// program that holds one document.
/// </summary>
/// <param name="TextDocument">Any document of the program.</param>
internal sealed record DirectPagesParams(TextDocumentIdentifier TextDocument);
