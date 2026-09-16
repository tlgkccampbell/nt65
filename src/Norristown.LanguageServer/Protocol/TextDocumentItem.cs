namespace Norristown.LanguageServer.Protocol;

/// <summary>A document the client has just opened, with its text.</summary>
/// <param name="Uri">The document's URI.</param>
/// <param name="LanguageId">The client's language id, <c>nt65</c> for ours.</param>
/// <param name="Version">The revision this text is.</param>
/// <param name="Text">The whole text.</param>
internal sealed record TextDocumentItem(string Uri, string LanguageId, int Version, string Text);
