using Norristown.Syntax;

namespace Norristown.Processor;

/// <summary>Converts between CPUs and their names in nt65 source and in the ca65 output.</summary>
public static class CpuNames
{
    /// <summary>Gets every CPU, in the order nt65 lists them.</summary>
    public static IReadOnlyList<Cpu> All { get; } = Enum.GetValues<Cpu>();

    /// <summary>
    /// Gets the CPU names as a message lists them, such as <c>6502</c> and <c>65sc02</c>. The
    /// lexer recognizes CPU names without knowing about <see cref="Cpu"/>, so it owns the
    /// names, and a message about a name it rejected lists exactly the ones it accepts.
    /// </summary>
    public static string Listed => SyntaxFacts.ListedCpuNames;

    /// <summary>Returns the CPU that <paramref name="text"/> names, or null if it names none.</summary>
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

    /// <summary>Returns the name nt65 uses for <paramref name="cpu"/>.</summary>
    public static string Spell(Cpu cpu) => cpu switch
    {
        Cpu.Mos6502 => "6502",
        Cpu.Mos6502X => "6502x",
        Cpu.Cmos65SC02 => "65sc02",
        Cpu.Rockwell65C02 => "r65c02",
        Cpu.Wdc65C02 => "65c02",
        _ => "65816",
    };

    /// <summary>
    /// Returns the name ca65's <c>.setcpu</c> takes for <paramref name="cpu"/>, chosen so that
    /// ca65's instruction set under it is exactly the CPU's.
    /// </summary>
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
