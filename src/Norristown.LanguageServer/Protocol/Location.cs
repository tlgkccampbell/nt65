namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents a range in a named document.</summary>
/// <param name="Uri">The document.</param>
/// <param name="Range">The range within it.</param>
internal sealed record Location(string Uri, Range Range);
