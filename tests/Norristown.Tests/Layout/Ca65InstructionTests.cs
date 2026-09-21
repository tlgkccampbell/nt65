using System.Text.RegularExpressions;
using Norristown.Layout;
using Norristown.Project;

namespace Norristown.Tests.Layout;

/// <summary>
/// The words ca65 reads as an instruction, held against ca65's own tables in the pinned
/// source. It is how the alias spellings were missed: the list was nt65's idea of the
/// processor rather than the assembler's of its input, so this reads the assembler's.
/// <para>
/// It needs no assembler run, only the source checkout <c>scripts/build-cc65.ps1</c> makes,
/// which is why it is an oracle test: the gate builds cc65 before it runs them.
/// </para>
/// </summary>
[Trait("Category", "Oracle")]
public sealed partial class Ca65InstructionTests
{
    /// <summary>A row of one of ca65's instruction tables, which begins with the mnemonic in quotes.</summary>
    [GeneratedRegex(@"^\s*\{\s*""(?<name>[A-Za-z0-9]+)""")]
    private static partial Regex Row();

    /// <summary>Where one of the tables begins: the name follows the struct it closes.</summary>
    [GeneratedRegex(@"^\}\s*(?<table>InsTab\w+)\s*=\s*\{")]
    private static partial Regex Table();

    /// <summary>
    /// For every CPU, the words the emitter would prefix are exactly ca65's table for the
    /// <c>.setcpu</c> nt65 writes. A ca65 that gains an instruction fails this when the pin
    /// moves, which is when there is something to decide.
    /// </summary>
    [Fact]
    public void TheWordsNt65PrefixesAreCa65sOwnTable()
    {
        var tables = Tables();
        var problems = new List<string>();
        foreach (var cpu in CpuNames.All)
        {
            var setcpu = CpuNames.SpellForCa65(cpu);
            Assert.True(tables.TryGetValue("InsTab" + setcpu, out var theirs),
                $"ca65's instr.c has no table InsTab{setcpu}");
            var ours = Ca65Instructions.Of(cpu);
            problems.AddRange(theirs!.Except(ours, StringComparer.Ordinal)
                .Select(name => $"`.setcpu \"{setcpu}\"`: ca65 has `{name}` and nt65 does not"));
            problems.AddRange(ours.Except(theirs!, StringComparer.Ordinal)
                .Select(name => $"`.setcpu \"{setcpu}\"`: nt65 has `{name}` and ca65 does not"));
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems.Order(StringComparer.Ordinal)));
    }

    /// <summary>Each table of ca65's <c>instr.c</c>, by its name, holding its mnemonics in lower case.</summary>
    private static Dictionary<string, List<string>> Tables()
    {
        var path = Repo.Path(".cache", "cc65-src", "src", "ca65", "instr.c");
        Assert.True(File.Exists(path), $"the pinned cc65 source is not at {path}; run scripts/build-cc65.ps1");

        var tables = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? current = null;
        foreach (var line in Repo.ReadText(path).ReplaceLineEndings("\n").Split('\n'))
        {
            if (Table().Match(line) is { Success: true } opened)
                tables[opened.Groups["table"].Value] = current = [];
            else if (line.StartsWith("};", StringComparison.Ordinal))
                current = null;
            else if (current is not null && Row().Match(line) is { Success: true } row)
                current.Add(row.Groups["name"].Value.ToLowerInvariant());
        }
        return tables;
    }
}
