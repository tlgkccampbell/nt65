namespace Norristown.Project;

/// <summary>How a CPU is written: in nt65 source, and in the ca65 output.</summary>
public static class CpuNames
{
    /// <summary>The CPU a name stands for, or null when it names none.</summary>
    public static Cpu? Parse(string text) => text.ToLowerInvariant() switch
    {
        "6502" => Cpu.Mos6502,
        "65c02" => Cpu.Wdc65C02,
        "65816" => Cpu.Wdc65816,
        _ => null,
    };

    /// <summary>The name nt65 writes.</summary>
    public static string Spell(Cpu cpu) => cpu switch
    {
        Cpu.Mos6502 => "6502",
        Cpu.Wdc65C02 => "65c02",
        _ => "65816",
    };

    /// <summary>The name ca65's <c>.setcpu</c> takes.</summary>
    public static string SpellForCa65(Cpu cpu) => cpu switch
    {
        Cpu.Mos6502 => "6502",
        Cpu.Wdc65C02 => "W65C02",
        _ => "65816",
    };

    /// <summary>Whether <paramref name="cpu"/> has everything <paramref name="other"/> has.</summary>
    public static bool Includes(Cpu cpu, Cpu other) => cpu >= other;
}
