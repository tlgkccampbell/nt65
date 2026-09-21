namespace Norristown.Project;

/// <summary>How a CPU is written: in nt65 source, and in the ca65 output.</summary>
public static class CpuNames
{
    /// <summary>Every CPU, in the order nt65 lists them.</summary>
    public static IReadOnlyList<Cpu> All { get; } = Enum.GetValues<Cpu>();

    /// <summary>The names, as a message lists them: <c>6502</c>, <c>65sc02</c>, and so on.</summary>
    public static string Listed { get; } =
        string.Join(", ", All.SkipLast(1).Select(cpu => $"`{Spell(cpu)}`")) + $" or `{Spell(All[^1])}`";

    /// <summary>The CPU a name stands for, or null when it names none.</summary>
    public static Cpu? Parse(string text) => text.ToLowerInvariant() switch
    {
        "6502" => Cpu.Mos6502,
        "6502x" => Cpu.Mos6502X,
        "65sc02" => Cpu.Cmos65SC02,
        "r65c02" => Cpu.Rockwell65C02,
        "65c02" => Cpu.Wdc65C02,
        "65816" => Cpu.Wdc65816,
        _ => null,
    };

    /// <summary>The name nt65 writes.</summary>
    public static string Spell(Cpu cpu) => cpu switch
    {
        Cpu.Mos6502 => "6502",
        Cpu.Mos6502X => "6502x",
        Cpu.Cmos65SC02 => "65sc02",
        Cpu.Rockwell65C02 => "r65c02",
        Cpu.Wdc65C02 => "65c02",
        _ => "65816",
    };

    /// <summary>The name ca65's <c>.setcpu</c> takes, whose instruction set is exactly the CPU's.</summary>
    public static string SpellForCa65(Cpu cpu) => cpu switch
    {
        Cpu.Mos6502 => "6502",
        Cpu.Mos6502X => "6502X",
        Cpu.Cmos65SC02 => "65SC02",
        Cpu.Rockwell65C02 => "65C02",
        Cpu.Wdc65C02 => "W65C02",
        _ => "65816",
    };
}
