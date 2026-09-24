namespace Norristown.LanguageServer.Protocol;

/// <summary>Describes what the server wants to be told about open documents.</summary>
/// <param name="OpenClose">Whether the client sends <c>didOpen</c> and <c>didClose</c>.</param>
/// <param name="Change">How the client sends edits.</param>
internal sealed record TextDocumentSyncOptions(bool OpenClose, TextDocumentSyncKind Change);
