namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents a range in a document. <see cref="End"/> is exclusive.</summary>
/// <param name="Start">The start position.</param>
/// <param name="End">The end position.</param>
internal sealed record Range(Position Start, Position End);
