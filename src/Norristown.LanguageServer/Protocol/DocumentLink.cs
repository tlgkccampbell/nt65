namespace Norristown.LanguageServer.Protocol;

/// <summary>A range of the document that opens something else when it is clicked.</summary>
/// <param name="Range">The text that is the link.</param>
/// <param name="Target">What clicking it opens, as a URI.</param>
internal sealed record DocumentLink(Range Range, string Target);
