using System.Text.RegularExpressions;
using Norristown.Layout;
using Norristown.Project;

namespace Norristown.Tests.Emit;

/// <summary>
/// What the output defines at the margin, held against the words ca65 reads as instructions
/// under the <c>.setcpu</c> the output itself writes. ca65 takes one of those at the start of
/// a line for an instruction, so a bare definition under such a name is output ca65 refuses,
/// which is an nt65 bug by the contract of §1.
/// <para>
/// It runs over what a caller has already compiled — every fixture, every corpus program —
/// rather than compiling anything of its own, so it costs the suite nothing.
/// </para>
/// </summary>
internal static partial class BareNames
{
    /// <summary>A name defined at the margin: a label, an assignment, or the <c>z := *</c> a label named `z` becomes.</summary>
    [GeneratedRegex(@"^(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?::=|:|=)")]
    private static partial Regex Defined();

    /// <summary>The <c>.setcpu</c> the output's own header writes.</summary>
    [GeneratedRegex(@"^\.setcpu\s+""(?<cpu>[^""]+)""")]
    private static partial Regex SetCpu();

    /// <summary>Every way <paramref name="text"/> defines a name ca65 would read as an instruction.</summary>
    public static IEnumerable<string> Problems(string label, string path, string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        if (Cpu(lines) is not { } cpu)
            yield break;
        var instructions = Ca65Instructions.Of(cpu);
        for (var i = 0; i < lines.Length; i++)
        {
            if (Defined().Match(lines[i]) is { Success: true } definition
                && instructions.Contains(definition.Groups["name"].Value))
            {
                yield return $"[{label}] {path}:{i + 1}: `{definition.Groups["name"].Value}` is an instruction "
                    + $"to ca65 under `.setcpu \"{CpuNames.SpellForCa65(cpu)}\"`, and this line defines it:\n  {lines[i]}";
            }
        }
    }

    /// <summary>The CPU the output says it is for, or null for a file with no header, such as a line map.</summary>
    private static Cpu? Cpu(string[] lines)
    {
        foreach (var line in lines)
        {
            if (SetCpu().Match(line) is { Success: true } header)
            {
                return CpuNames.All.Cast<Cpu?>()
                    .FirstOrDefault(cpu => CpuNames.SpellForCa65(cpu!.Value) == header.Groups["cpu"].Value);
            }
        }
        return null;
    }
}
