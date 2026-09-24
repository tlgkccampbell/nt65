namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents a document the client has just opened, with its text.</summary>
/// <param name="Uri">The document's URI.</param>
/// <param name="LanguageId">The client's language id, which is <c>nt65</c> for nt65 documents.</param>
/// <param name="Version">The version of this text.</param>
/// <param name="Text">The whole text.</param>
internal sealed record TextDocumentItem(string Uri, string LanguageId, int Version, string Text);
