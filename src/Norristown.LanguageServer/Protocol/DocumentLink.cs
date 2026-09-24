namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents a range of the document that opens another resource when clicked.</summary>
/// <param name="Range">The text that forms the link.</param>
/// <param name="Target">The URI that clicking the link opens.</param>
internal sealed record DocumentLink(Range Range, string Target);
