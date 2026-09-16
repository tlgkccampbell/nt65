namespace Norristown.Project;

/// <summary>The processor a program is built for (§5.1). One per program.</summary>
public enum Cpu
{
    /// <summary>The MOS 6502.</summary>
    Mos6502,

    /// <summary>The WDC 65C02, which adds to the 6502.</summary>
    Wdc65C02,

    /// <summary>The WDC 65816, which adds to the 65C02.</summary>
    Wdc65816,
}
