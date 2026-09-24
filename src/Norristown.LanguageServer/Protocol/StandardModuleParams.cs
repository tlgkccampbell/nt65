namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Parameters of the <c>nt65/standardModule</c> request, which asks for the source of a module
/// that comes with nt65.
/// </summary>
/// <param name="TextDocument">
/// The module, named by the <c>nt65:</c> URI that a location into it gave.
/// </param>
internal sealed record StandardModuleParams(TextDocumentIdentifier TextDocument);
