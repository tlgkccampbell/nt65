namespace Norristown.LanguageServer.Protocol;

/// <summary><c>nt65/standardModule</c>: the source of a module that comes with nt65.</summary>
/// <param name="TextDocument">The module, by the <c>nt65:</c> URI a location into it gave.</param>
internal sealed record StandardModuleParams(TextDocumentIdentifier TextDocument);
