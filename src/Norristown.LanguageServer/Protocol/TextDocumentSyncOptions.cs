namespace Norristown.LanguageServer.Protocol;

/// <summary>What the server wants to be told about open documents.</summary>
/// <param name="OpenClose">Whether to send <c>didOpen</c> and <c>didClose</c>.</param>
/// <param name="Change">How to send edits.</param>
internal sealed record TextDocumentSyncOptions(bool OpenClose, TextDocumentSyncKind Change);
