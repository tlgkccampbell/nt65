namespace Norristown.Processor;

/// <summary>
/// Specifies the bits of the status register, as a <c>rep</c> or a <c>sep</c> mask names them.
/// <see cref="M"/> and <see cref="X"/> are the 65816's width flags in native mode.
/// </summary>
[Flags]
public enum StatusFlags
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary>The carry flag, C.</summary>
    Carry = 0x01,

    /// <summary>The zero flag, Z.</summary>
    Zero = 0x02,

    /// <summary>The interrupt disable flag, I.</summary>
    InterruptDisable = 0x04,

    /// <summary>The decimal flag, D.</summary>
    Decimal = 0x08,

    /// <summary>The index width flag, which makes X and Y 8 bits when set.</summary>
    X = 0x10,

    /// <summary>The accumulator width flag, which makes A 8 bits when set.</summary>
    M = 0x20,

    /// <summary>The overflow flag, V.</summary>
    Overflow = 0x40,

    /// <summary>The negative flag, N.</summary>
    Negative = 0x80,
}
