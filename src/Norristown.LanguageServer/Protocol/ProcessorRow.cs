namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents one row of an <c>nt65/processor</c> answer, which is one fact about the processor.</summary>
/// <param name="Key">What the row is about, such as <c>A</c>, <c>flags</c> or <c>3,s</c>.</param>
/// <param name="Value">What is known of it, which says <c>unknown</c> rather than being left out.</param>
/// <param name="Detail">Where the value came from, such as the line that set it, or null.</param>
/// <param name="Target">The place the row leads to when chosen, or null.</param>
/// <param name="Rows">The rows under this one, or null where it has none.</param>
internal sealed record ProcessorRow(
    string Key, string Value, string? Detail, Location? Target, IReadOnlyList<ProcessorRow>? Rows);
