namespace Norristown.Semantics;

/// <summary>
/// One segment of the program: its name and the address size every symbol in it
/// gets. A segment is declared exactly once, so this is what references to it are sized from.
/// </summary>
/// <param name="Name">The name as it is written in quotes.</param>
/// <param name="Size">The address size of symbols in the segment.</param>
/// <param name="Declaration">Where it was declared, or null for one of the standard names.</param>
/// <param name="DirectPage">
/// The <c>dp = e</c> it declares: the direct page its symbols are meant to be reached through,
/// on the 65816. Null where it declares none, and nothing about D is then checked against it.
/// </param>
/// <param name="Bank">The <c>bank = e</c> it declares: the bank it lives in, on the 65816. Null where it declares none.</param>
public sealed record Segment(string Name, AddressSize Size, Span? Declaration, long? DirectPage = null, long? Bank = null);
