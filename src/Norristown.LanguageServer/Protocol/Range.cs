namespace Norristown.LanguageServer.Protocol;

/// <summary>A range in a document; <see cref="End"/> is exclusive.</summary>
/// <param name="Start">Where it starts.</param>
/// <param name="End">Where it ends.</param>
internal sealed record Range(Position Start, Position End);
