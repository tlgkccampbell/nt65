namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents one instruction that reaches a location.</summary>
/// <param name="Place">The instruction's whole line.</param>
/// <param name="Reads">Whether it reads the location.</param>
/// <param name="Writes">Whether it writes the location.</param>
/// <param name="Times">How many times one pass through its routine runs it, counting only loops whose counts are known.</param>
/// <param name="Uncounted">Whether it is inside a loop whose count is not known.</param>
internal sealed record DirectPageAccess(Location Place, bool Reads, bool Writes, long Times, bool Uncounted);
