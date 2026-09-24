namespace Norristown.Semantics;

/// <summary>
/// Represents how much room a data directive takes, as the number of bytes it generates and the
/// number of elements those bytes form. A label on the directive gets both values, which
/// <c>.sizeof</c> and <c>.countof</c> return.
/// </summary>
/// <param name="Bytes">The number of bytes generated.</param>
/// <param name="Elements">The number of elements, which is one per value, or one per byte for text.</param>
public readonly record struct DataSize(long Bytes, long Elements);
