namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents a routine that takes a location's address without reaching it.</summary>
/// <param name="Name">The routine's name.</param>
/// <param name="Declaration">Where the routine's name is declared.</param>
/// <param name="Places">The routine's lines that take the address.</param>
internal sealed record DirectPageReferrer(string Name, Location Declaration, IReadOnlyList<Location> Places);
