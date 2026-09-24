namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents text that a client renders.</summary>
/// <param name="Kind">The format, which is either <c>plaintext</c> or <c>markdown</c>.</param>
/// <param name="Value">The text.</param>
internal sealed record MarkupContent(string Kind, string Value)
{
    /// <summary>Returns Markdown content holding <paramref name="value"/>, the format nt65 uses.</summary>
    public static MarkupContent Markdown(string value) => new("markdown", value);
}
