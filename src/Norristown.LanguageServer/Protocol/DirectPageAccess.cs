namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents one instruction that reaches a location.</summary>
/// <param name="Place">The instruction's whole line.</param>
/// <param name="Reads">Whether it reads the location.</param>
/// <param name="Writes">Whether it writes the location.</param>
internal sealed record DirectPageAccess(Location Place, bool Reads, bool Writes);
