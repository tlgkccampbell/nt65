using System.Text.RegularExpressions;
using Norristown.Processor;

namespace Norristown.Tests.Emit;

/// <summary>
/// Checks the names the output defines at the start of a line against the words ca65 reads as
/// instructions under the <c>.setcpu</c> the output itself writes. ca65 reads such a word at the
/// start of a line as an instruction, so a bare definition with that name is output ca65
/// rejects, and output ca65 rejects is always an nt65 bug.
/// <para>
/// The check runs over output a caller has already compiled, such as every fixture and every
/// corpus program. It compiles nothing of its own, so it adds no compilation to the suite.
/// </para>
/// </summary>
internal static partial class BareNames
{
    /// <summary>
    /// Returns a problem for each line of <paramref name="text"/> that defines a name ca65 would
    /// read as an instruction.
    /// </summary>
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
                    + $"to ca65 under `.setcpu \"{CpuNames.FormatForCa65(cpu)}\"`, and this line defines it:\n  {lines[i]}";
            }
        }
    }

    /// <summary>
    /// Matches a name defined at the start of a line. The definition is a label, an assignment,
    /// or the <c>z := *</c> that nt65 writes for a label named <c>z</c>.
    /// </summary>
    [GeneratedRegex(@"^(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?::=|:|=)")]
    private static partial Regex Defined();

    /// <summary>Matches the <c>.setcpu</c> line that the output's own header writes.</summary>
    [GeneratedRegex(@"^\.setcpu\s+""(?<cpu>[^""]+)""")]
    private static partial Regex SetCpu();

    /// <summary>
    /// Returns the CPU that the output's header names, or null if the file has no header. A line
    /// map, for example, has none.
    /// </summary>
    private static Cpu? Cpu(string[] lines)
    {
        foreach (var line in lines)
        {
            if (SetCpu().Match(line) is { Success: true } header)
            {
                return CpuNames.All.Cast<Cpu?>()
                    .FirstOrDefault(cpu => CpuNames.FormatForCa65(cpu!.Value) == header.Groups["cpu"].Value);
            }
        }
        return null;
    }
}
