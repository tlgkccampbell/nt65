namespace Norristown.Processor;

/// <summary>The processor a program is built for. One per program.</summary>
public enum Cpu
{
    /// <summary>The MOS 6502.</summary>
    Mos6502,

    /// <summary>The NMOS 6502 with the undocumented opcodes ca65 assembles under <c>6502X</c>.</summary>
    Mos6502X,

    /// <summary>The original CMOS 6502, which adds to the 6502 and has neither the bit instructions nor <c>wai</c> and <c>stp</c>.</summary>
    Cmos65SC02,

    /// <summary>The Rockwell 65C02, which adds the bit instructions to the 65SC02.</summary>
    Rockwell65C02,

    /// <summary>The WDC 65C02, which adds <c>wai</c> and <c>stp</c> to the Rockwell set.</summary>
    Wdc65C02,

    /// <summary>The WDC 65816, which adds to the 65C02 and leaves out its bit instructions.</summary>
    Wdc65816,
}
