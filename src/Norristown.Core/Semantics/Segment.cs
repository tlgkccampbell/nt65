namespace Norristown.Semantics;

/// <summary>
/// One segment of the program: its name and the address size every symbol in it
/// gets. A segment is declared exactly once, so this is what references to it are sized from.
/// </summary>
/// <param name="Name">The name as it is written in quotes.</param>
/// <param name="Size">The address size of symbols in the segment.</param>
/// <param name="Declaration">Where it was declared, or null for one of the standard names.</param>
public sealed record Segment(string Name, AddressSize Size, Span? Declaration);
