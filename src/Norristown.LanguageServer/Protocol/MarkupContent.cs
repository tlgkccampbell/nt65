namespace Norristown.LanguageServer.Protocol;

/// <summary>Text a client renders.</summary>
/// <param name="Kind">Either <c>plaintext</c> or <c>markdown</c>.</param>
/// <param name="Value">The text.</param>
internal sealed record MarkupContent(string Kind, string Value)
{
    /// <summary>Markdown, which is what nt65 writes.</summary>
    public static MarkupContent Markdown(string value) => new("markdown", value);
}
