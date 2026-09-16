namespace Norristown.Semantics;

/// <summary>
/// How much room a data directive takes: the bytes it generates, and how many elements
/// those bytes are. A label written on it gets both, which is what <c>.sizeof</c> and
/// <c>.countof</c> answer.
/// </summary>
/// <param name="Bytes">The bytes generated.</param>
/// <param name="Elements">How many elements they are: one per value, or one per byte for text.</param>
public readonly record struct DataSize(long Bytes, long Elements);
